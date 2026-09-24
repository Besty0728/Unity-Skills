using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Every skill call a shipped doc shows must run as written: agents copy these shapes verbatim, so an example
    /// naming a missing skill or an unaccepted argument is an UNKNOWN_PARAM / SKILL_NOT_FOUND round trip handed out
    /// by the docs themselves. Scans every *.md under unity-skills~ (root, references/, skills/*/ and their
    /// reference/ folders) for four forms: the root quick-reference table (`skill {a, b}` code spans in table rows),
    /// <c>call_skill("name", k=v, ...)</c>, <c>/skills/batch</c> step objects <c>{"skill": "name", "args": {...}}</c>,
    /// and <c>/skill/name</c> URLs with a JSON body (<c>-d '{...}'</c>, or a JSON object on the next line).
    ///
    /// Top-level argument names go through <see cref="SkillRouter.ValidateParameters"/> (UnknownParams must be empty);
    /// values are never judged, and placeholders such as <c>&lt;name&gt;</c>, <c>...</c> or Python variables stand in as null.
    /// For <c>*_batch</c> skills, literal <c>items</c> keys are checked against the item type the skill hands to
    /// <see cref="BatchExecutor.Execute{TItem}"/>, found by decoding the skill method's IL (following calls into its
    /// declaring type and compiler-generated closures) and matched with the same Newtonsoft contract lookup
    /// BatchExecutor uses. Nested *Item class names cannot be matched to skills by name
    /// (material_set_colors_batch uses BatchColorItem, uitk_create_batch uses UitkFileItem), so the type argument is the only reliable link.
    /// </summary>
    [TestFixture]
    public class DocExampleValidationTests
    {
        private const string QuickReferenceSource = "quick reference";
        private const string CallSkillSource = "call_skill";
        private const string BatchStepSource = "batch step";
        private const string UrlSource = "/skill/ URL";

        private const string SkillNamePattern = @"[a-z][a-z0-9]*(?:_[a-z0-9]+)+";

        private static readonly Regex QuickReferenceCallRegex =
            new Regex(@"`(?<skill>" + SkillNamePattern + @")\s*\{(?<keys>[^}`]*)\}`", RegexOptions.Compiled);

        private static readonly Regex CallSkillRegex = new Regex(@"\bcall_skill\(", RegexOptions.Compiled);

        private static readonly Regex CallSkillNameRegex =
            new Regex(@"\G\s*(?<q>[""'])(?<skill>" + SkillNamePattern + @")\k<q>", RegexOptions.Compiled);

        private static readonly Regex BatchStepRegex =
            new Regex(@"(?<q1>[""'])skill\k<q1>\s*:\s*(?<q2>[""'])(?<skill>" + SkillNamePattern + @")\k<q2>", RegexOptions.Compiled);

        private static readonly Regex SkillUrlRegex =
            new Regex(@"/skill/(?<skill>" + SkillNamePattern + @")\b", RegexOptions.Compiled);

        private static readonly Regex CurlDataRegex =
            new Regex(@"(?:^|\s)(?:-d|--data(?:-raw|-binary)?)\s+['""]", RegexOptions.Compiled);

        /// <summary>Python call() keywords the client consumes itself instead of sending them to the server.</summary>
        private static readonly HashSet<string> PythonClientOnlyKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "wait_for_job", "job_timeout", "timeout", "_retries", "_retry_delay"
        };

        private static readonly HashSet<string> BatchStepKeys = new HashSet<string>(StringComparer.Ordinal) { "skill", "args" };

        [Test]
        public void DocExamples_NameRealSkillsAndAcceptedParameters()
        {
            var root = GetPackageDocsRoot();
            Assert.That(Directory.Exists(root), Is.True, $"unity-skills~ not found: {root}");

            var examples = new List<DocExample>();
            var unparsed = new List<string>();
            foreach (var path in Directory.GetFiles(root, "*.md", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                var relative = path.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
                var text = File.ReadAllText(path).Replace("\r\n", "\n");
                examples.AddRange(DocExampleExtractor.Extract(relative, text, relative == "SKILL.md", unparsed));
            }

            Assert.That(examples, Is.Not.Empty, "No skill call example was found in the docs, so nothing was checked.");
            Assert.That(examples.Any(e => e.File == "SKILL.md" && e.Source == QuickReferenceSource), Is.True,
                "The root SKILL.md quick-reference table yielded no call; its row format changed and the table is no longer checked.");

            var issues = new List<string>();
            var validationFallbacks = new List<string>();
            var unresolvedItemTypes = new SortedSet<string>(StringComparer.Ordinal);
            int itemExamples = 0, itemExamplesChecked = 0;

            foreach (var example in examples)
            {
                if (!SkillRouter.TryGetSkill(example.Skill, out var skill))
                {
                    issues.Add($"{example.Location}: `{example.Skill}` is not a registered skill.");
                    continue;
                }

                if (example.Args == null)
                    continue;

                foreach (var name in UnknownArgumentNames(skill, example, validationFallbacks))
                    issues.Add($"{example.Location}: `{example.Skill}` does not accept `{name}`.");

                var items = ReadLiteralItems(example);
                if (items == null)
                    continue;

                itemExamples++;
                var itemType = BatchItemTypeResolver.Resolve(skill.Method);
                var contract = itemType == null ? null : ResolveItemContract(itemType);
                if (contract == null)
                {
                    unresolvedItemTypes.Add(example.Skill);
                    continue;
                }

                itemExamplesChecked++;
                for (int index = 0; index < items.Count; index++)
                {
                    if (!(items[index] is JObject item))
                        continue;

                    foreach (var property in item.Properties())
                    {
                        var match = contract.Properties.GetClosestMatchProperty(property.Name);
                        if (match == null || match.Ignored)
                            issues.Add($"{example.Location}: `{example.Skill}` items[{index}].`{property.Name}` is not a field of {itemType.Name}.");
                    }
                }
            }

            Assert.That(itemExamples == 0 || itemExamplesChecked > 0, Is.True,
                $"{itemExamples} batch examples carry literal items, but no item type was resolved for any of them " +
                $"({string.Join(", ", unresolvedItemTypes)}); the IL lookup of BatchExecutor.Execute<T> has stopped working.");

            var bySource = string.Join(", ", examples.GroupBy(e => e.Source).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => $"{g.Key} {g.Count()}"));
            var notes = new StringBuilder()
                .AppendLine($"Checked {examples.Count} examples ({bySource}); items checked in {itemExamplesChecked}/{itemExamples} batch examples.");
            if (unresolvedItemTypes.Count > 0)
                notes.AppendLine("Item keys not checked (no BatchExecutor.Execute<T> in the skill): " + string.Join(", ", unresolvedItemTypes));
            if (unparsed.Count > 0)
                notes.AppendLine("Not parsed, skill name checked only: " + string.Join("; ", unparsed));
            if (validationFallbacks.Count > 0)
                notes.AppendLine("ValidateParameters threw, names checked against AllowedParameterSet: " + string.Join("; ", validationFallbacks));
            TestContext.WriteLine(notes.ToString());

            if (issues.Count > 0)
                Assert.Fail("Doc examples that cannot run as written (fix the example, not the skill):\n" +
                            string.Join("\n", issues) + "\n\n" + notes);
        }

        [Test]
        public void Extractor_ReadsEveryDocumentedCallForm()
        {
            const string sample =
                "| Move | `gameobject_set_transform {name, posX/Y/Z, localPosX}` \u2014 pos world |\n" +
                "```python\n" +
                "unity_skills.call_skill(\"gameobject_create_batch\", items=json.dumps([\n" +
                "    {\"name\": f\"Crate_{i}\", \"x\": i * 2},  # one per slot\n" +
                "    ...\n" +
                "]), verbose=True, wait_for_job=True)\n" +
                "u.call_skill(\"gameobject_duplicate\", name=template,\n" +
                "    newName=\"Copy\")\n" +
                "```\n" +
                "`POST /skills/batch` `{\"steps\":[{\"skill\":\"component_add\",\"args\":{\"name\":\"A\",\"componentType\":{\"$ref\":\"$0.type\"}}}]}`\n" +
                "`curl -s -X POST \"http://localhost:<port>/skill/material_assign?expectProject=<p>\" -d '{\"name\":\"A\",\"materialPath\":\"<path>\"}'`\n" +
                "Prose that mentions `unity_skills.call_skill(...)` and a response {\"index\":0,\"skill\":\"scene_save\",\"status\":\"success\"}.\n";

            var unparsed = new List<string>();
            var examples = DocExampleExtractor.Extract("sample.md", sample, true, unparsed);
            var shapes = examples.Select(e => $"{e.Line}:{e.Source}:{e.Skill}({(e.Args == null ? "?" : string.Join(",", e.Args.Properties().Select(p => p.Name)))})").ToArray();

            Assert.That(shapes, Is.EqualTo(new[]
            {
                "1:quick reference:gameobject_set_transform(name,posX,posY,posZ,localPosX)",
                "3:call_skill:gameobject_create_batch(items,verbose)",
                "7:call_skill:gameobject_duplicate(name,newName)",
                "10:batch step:component_add(name,componentType)",
                "11:/skill/ URL:material_assign(name,materialPath)",
            }), string.Join("\n", shapes));

            var items = (JArray)examples[1].Args["items"];
            Assert.That(items.Count, Is.EqualTo(2), items.ToString(Formatting.None));
            Assert.That(((JObject)items[0]).Properties().Select(p => p.Name), Is.EqualTo(new[] { "name", "x" }));
            Assert.That(unparsed, Is.Empty, string.Join("; ", unparsed));
        }

        [TestCase("gameobject_create_batch", "BatchCreateItem")]
        [TestCase("component_set_property_batch", "BatchSetPropertyItem")]
        [TestCase("material_set_colors_batch", "BatchColorItem")]
        public void BatchItemType_IsTheExecuteTypeArgument(string skillName, string expectedType)
        {
            Assume.That(SkillRouter.TryGetSkill(skillName, out var skill), Is.True, $"{skillName} is not registered.");

            Assert.That(BatchItemTypeResolver.Resolve(skill.Method)?.Name, Is.EqualTo(expectedType));
        }

        /// <summary>
        /// The argument names ValidateParameters reports as unknown. It is the router's own check, so reserved body
        /// parameters and the synthetic entityId count as accepted exactly as they do at execution time. A planner
        /// that throws on a placeholder value must not hide the name check, so that case falls back to the same
        /// AllowedParameterSet the router consults.
        /// </summary>
        private static IEnumerable<string> UnknownArgumentNames(SkillRouter.SkillInfo skill, DocExample example, List<string> fallbacks)
        {
            try
            {
                var validation = SkillRouter.ValidateParameters(skill, example.Args.ToString(Formatting.None));
                return validation.UnknownParams.Select(ReadParameterName).ToList();
            }
            catch (Exception ex)
            {
                fallbacks.Add($"{example.Location} {ex.GetType().Name}");
                return example.Args.Properties().Select(p => p.Name).Where(name => !skill.AllowedParameterSet.Contains(name)).ToList();
            }
            finally
            {
                GameObjectFinder.DrainResolutionNotes();
            }
        }

        private static string ReadParameterName(object entry)
        {
            if (entry is IDictionary<string, object> dictionary && dictionary.TryGetValue("parameter", out var name))
                return name?.ToString();
            return JObject.FromObject(entry)["parameter"]?.ToString();
        }

        /// <summary>A literal items array of a *_batch example (native array, or a JSON string holding one); null otherwise.</summary>
        private static JArray ReadLiteralItems(DocExample example)
        {
            if (!example.Skill.EndsWith("_batch", StringComparison.Ordinal) ||
                !example.Args.TryGetValue("items", StringComparison.OrdinalIgnoreCase, out var token))
                return null;

            if (token is JArray array)
                return array;
            if (token.Type != JTokenType.String)
                return null;
            try { return JToken.Parse(token.Value<string>()) as JArray; }
            catch (JsonException) { return null; }
        }

        /// <summary>Same resolution as BatchExecutor's unknown-field warning: null when unknown keys cannot be judged.</summary>
        private static JsonObjectContract ResolveItemContract(Type itemType)
        {
            var contract = JsonSerializer.CreateDefault().ContractResolver.ResolveContract(itemType) as JsonObjectContract;
            return contract == null || contract.ExtensionDataSetter != null ? null : contract;
        }

        private static string GetPackageDocsRoot()
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            var inProject = projectRoot == null ? null : Path.Combine(projectRoot, "SkillsForUnity", "unity-skills~");
            if (inProject != null && Directory.Exists(inProject))
                return inProject;

            var package = PackageInfo.FindForAssembly(typeof(UnitySkillAttribute).Assembly);
            if (package != null)
            {
                var inPackage = Path.Combine(package.resolvedPath, "unity-skills~");
                if (Directory.Exists(inPackage))
                    return inPackage;
            }

            return inProject;
        }

        private sealed class DocExample
        {
            public string File;
            public int Line;
            public string Skill;
            public string Source;
            /// <summary>The arguments as written; null when the example shows none that can be read.</summary>
            public JObject Args;

            public string Location => $"{File}:{Line} [{Source}]";
        }

        private static class DocExampleExtractor
        {
            public static List<DocExample> Extract(string file, string text, bool isRootDoc, List<string> unparsed)
            {
                var lineStarts = LineStarts(text);
                var examples = new List<DocExample>();

                void Add(int index, string skill, JObject args, string source) => examples.Add(new DocExample
                {
                    File = file, Line = LineOf(lineStarts, index), Skill = skill, Source = source, Args = args
                });

                if (isRootDoc)
                    ExtractQuickReference(text, lineStarts, Add);
                ExtractCallSkill(file, text, lineStarts, unparsed, Add);
                ExtractBatchSteps(file, text, lineStarts, unparsed, Add);
                ExtractSkillUrls(text, Add);

                return examples.OrderBy(e => e.Line).ThenBy(e => e.Source, StringComparer.Ordinal).ToList();
            }

            private static void ExtractQuickReference(string text, List<int> lineStarts, Action<int, string, JObject, string> add)
            {
                for (int line = 0; line < lineStarts.Count; line++)
                {
                    int start = lineStarts[line];
                    int end = line + 1 < lineStarts.Count ? lineStarts[line + 1] : text.Length;
                    var row = text.Substring(start, end - start);
                    if (!row.TrimStart().StartsWith("|", StringComparison.Ordinal))
                        continue;

                    foreach (Match match in QuickReferenceCallRegex.Matches(row))
                    {
                        var args = new JObject();
                        foreach (var key in ReadQuickReferenceKeys(match.Groups["keys"].Value))
                            args[key] = JValue.CreateNull();
                        add(start + match.Index, match.Groups["skill"].Value, args, QuickReferenceSource);
                    }
                }
            }

            /// <summary>Comma-separated names; <c>posX/Y/Z</c> expands to posX, posY, posZ and <c>r/g/b</c> to r, g, b.</summary>
            private static IEnumerable<string> ReadQuickReferenceKeys(string keys)
            {
                foreach (var raw in keys.Split(','))
                {
                    var key = raw.Split(':', '=')[0].Trim().Trim('"', '\'');
                    if (key.Length == 0 || key.StartsWith("...", StringComparison.Ordinal) || key[0] == '\u2026' || key[0] == '<')
                        continue;

                    var parts = key.Split('/');
                    var stem = parts[0].Trim();
                    yield return stem;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var part = parts[i].Trim();
                        bool axis = part.Length == 1 && char.IsUpper(part[0]) && stem.Length > 1 && char.IsUpper(stem[stem.Length - 1]);
                        yield return axis ? stem.Substring(0, stem.Length - 1) + part : part;
                    }
                }
            }

            private static void ExtractCallSkill(string file, string text, List<int> lineStarts, List<string> unparsed,
                Action<int, string, JObject, string> add)
            {
                foreach (Match match in CallSkillRegex.Matches(text))
                {
                    var name = CallSkillNameRegex.Match(text, match.Index + match.Length);
                    if (!name.Success)
                        continue;

                    if (LooseLiteral.TryParseCall(text, match.Index + match.Length, out var keywords))
                    {
                        foreach (var clientOnly in PythonClientOnlyKeywords)
                            keywords.Remove(clientOnly);
                        add(match.Index, name.Groups["skill"].Value, keywords, CallSkillSource);
                    }
                    else
                    {
                        unparsed.Add($"{file}:{LineOf(lineStarts, match.Index)}");
                        add(match.Index, name.Groups["skill"].Value, null, CallSkillSource);
                    }
                }
            }

            /// <summary>Objects whose only keys are skill and args; a response entry (index, status, result) is not a call.</summary>
            private static void ExtractBatchSteps(string file, string text, List<int> lineStarts, List<string> unparsed,
                Action<int, string, JObject, string> add)
            {
                foreach (Match match in BatchStepRegex.Matches(text))
                {
                    int open = FindEnclosingBrace(text, match.Index);
                    if (open < 0)
                        continue;

                    if (!LooseLiteral.TryParse(text, open, out var token) || !(token is JObject step))
                    {
                        unparsed.Add($"{file}:{LineOf(lineStarts, match.Index)}");
                        add(match.Index, match.Groups["skill"].Value, null, BatchStepSource);
                        continue;
                    }

                    if (step.Properties().Any(p => !BatchStepKeys.Contains(p.Name)))
                        continue;

                    var args = step["args"];
                    add(match.Index, match.Groups["skill"].Value, args == null ? new JObject() : args as JObject, BatchStepSource);
                }
            }

            private static void ExtractSkillUrls(string text, Action<int, string, JObject, string> add)
            {
                foreach (Match match in SkillUrlRegex.Matches(text))
                {
                    int lineStart = text.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
                    int commandEnd = CommandEnd(text, match.Index);
                    var command = text.Substring(lineStart, commandEnd - lineStart);

                    int body = -1;
                    var data = CurlDataRegex.Match(command);
                    if (data.Success)
                    {
                        int brace = text.IndexOf('{', lineStart + data.Index + data.Length - 1);
                        if (brace >= 0 && brace < commandEnd)
                            body = brace;
                    }
                    else
                    {
                        body = NextLineObjectStart(text, commandEnd);
                    }

                    JObject args = null;
                    if (body >= 0 && LooseLiteral.TryParse(text, body, out var token))
                        args = token as JObject;
                    add(match.Index, match.Groups["skill"].Value, args, UrlSource);
                }
            }

            /// <summary>End of the line holding index, extended over shell line continuations.</summary>
            private static int CommandEnd(string text, int index)
            {
                int end = text.IndexOf('\n', index);
                while (end > 0 && text[end - 1] == '\\')
                {
                    int next = text.IndexOf('\n', end + 1);
                    end = next < 0 ? text.Length : next;
                }
                return end < 0 ? text.Length : end;
            }

            /// <summary>The body of a bare <c>POST /skill/name</c> line: a JSON object starting the next line.</summary>
            private static int NextLineObjectStart(string text, int lineEnd)
            {
                int i = lineEnd + 1;
                while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
                    i++;
                return i < text.Length && text[i] == '{' ? i : -1;
            }

            private static int FindEnclosingBrace(string text, int index)
            {
                int depth = 0;
                for (int i = index - 1; i >= 0; i--)
                {
                    if (text[i] == '}')
                        depth++;
                    else if (text[i] == '{' && depth-- == 0)
                        return i;
                }
                return -1;
            }

            private static List<int> LineStarts(string text)
            {
                var starts = new List<int> { 0 };
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '\n')
                        starts.Add(i + 1);
                }
                return starts;
            }

            private static int LineOf(List<int> lineStarts, int index)
            {
                int found = lineStarts.BinarySearch(index);
                return (found >= 0 ? found : ~found - 1) + 1;
            }
        }

        /// <summary>
        /// A tolerant reader for the literals docs write: JSON, Python literals (single quotes, True/None, f-strings,
        /// comments, json.dumps(...), comprehensions) and shell-quoted bodies. Only structure and key names matter,
        /// so anything that is not a plain literal (a variable, an expression, <c>&lt;placeholder&gt;</c>, <c>...</c>)
        /// becomes null.
        /// </summary>
        private sealed class LooseLiteral
        {
            private const string StringPrefixes = "fFrRbBuU";
            private static readonly Regex NumberRegex = new Regex(@"\G-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?", RegexOptions.Compiled);
            private static readonly Regex IdentifierRegex = new Regex(@"\G[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.Compiled);
            private static readonly Regex KeyRegex = new Regex(@"\G[A-Za-z_$][A-Za-z0-9_$]*", RegexOptions.Compiled);
            private static readonly Regex KeywordRegex = new Regex(@"\G([A-Za-z_][A-Za-z0-9_]*)\s*=(?!=)", RegexOptions.Compiled);

            private readonly string _text;
            private int _pos;

            private LooseLiteral(string text, int pos)
            {
                _text = text;
                _pos = pos;
            }

            public static bool TryParse(string text, int pos, out JToken value)
            {
                try
                {
                    value = new LooseLiteral(text, pos).Value(0);
                    return true;
                }
                catch (FormatException)
                {
                    value = null;
                    return false;
                }
            }

            /// <summary>Reads <c>"name", k=v, ...)</c> after <c>call_skill(</c>; positional arguments and **spreads are skipped.</summary>
            public static bool TryParseCall(string text, int pos, out JObject keywords)
            {
                keywords = null;
                var parser = new LooseLiteral(text, pos);
                try
                {
                    parser.SkipTrivia();
                    if (!parser.AtString())
                        return false;
                    parser.ReadString();

                    var result = new JObject();
                    while (!parser.Consume(")"))
                    {
                        if (parser.Consume(","))
                            continue;
                        if (parser.Peek() == '\0')
                            return false;
                        if (parser.Consume("**"))
                        {
                            parser.Value(1);
                            continue;
                        }

                        var keyword = KeywordRegex.Match(text, parser._pos);
                        if (keyword.Success)
                            parser._pos += keyword.Length;
                        var value = parser.Value(1);
                        if (keyword.Success)
                            result[keyword.Groups[1].Value] = value;
                    }

                    keywords = result;
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }
            }

            private JToken Value(int depth)
            {
                if (depth > 64)
                    throw new FormatException("nesting too deep");

                var primary = Primary(depth);
                if (depth == 0 || IsDelimiter(Peek()))
                    return primary;

                // An expression tail (operators, a comprehension, string concatenation) up to the next delimiter:
                // a container keeps its parsed shape, a scalar becomes a placeholder.
                SkipToDelimiter();
                return primary is JContainer ? primary : JValue.CreateNull();
            }

            private JToken Primary(int depth)
            {
                char c = Peek();
                if (c == '\0' || IsDelimiter(c))
                    throw new FormatException("value expected");
                if (c == '{')
                    return Object(depth);
                if (c == '[' || c == '(')
                    return Array(depth, c == '[' ? "]" : ")");
                if (AtString())
                    return new JValue(ReadString());
                if (Consume("...") || Consume("\u2026") || SkipAnglePlaceholder())
                    return JValue.CreateNull();

                var number = NumberRegex.Match(_text, _pos);
                if (number.Success)
                {
                    _pos += number.Length;
                    return double.TryParse(number.Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? new JValue(parsed) : JValue.CreateNull();
                }

                var identifier = IdentifierRegex.Match(_text, _pos);
                if (!identifier.Success)
                {
                    if (depth == 0)
                        throw new FormatException($"unexpected '{c}'");
                    SkipToDelimiter();
                    return JValue.CreateNull();
                }

                _pos += identifier.Length;
                switch (identifier.Value)
                {
                    case "true":
                    case "True":
                        return new JValue(true);
                    case "false":
                    case "False":
                        return new JValue(false);
                    case "null":
                    case "None":
                        return JValue.CreateNull();
                }

                if (Peek() == '(' && (identifier.Value == "json.dumps" || identifier.Value == "dumps"))
                {
                    _pos++;
                    var inner = Value(depth + 1);
                    SkipRestOfCall();
                    return inner;
                }

                // A variable, a call or a subscript: not a literal.
                while (Peek() == '(' || Peek() == '[')
                    SkipBalanced();
                return JValue.CreateNull();
            }

            private JObject Object(int depth)
            {
                _pos++;
                var result = new JObject();
                while (!Consume("}"))
                {
                    if (Consume(",") || Consume("...") || Consume("\u2026"))
                        continue;
                    if (Peek() == '\0')
                        throw new FormatException("unterminated object");
                    if (Consume("**"))
                    {
                        Value(depth + 1);
                        continue;
                    }

                    var key = AtString() ? ReadString() : ReadKey();
                    if (!Consume(":"))
                        throw new FormatException("':' expected");
                    result[key] = Value(depth + 1);
                }
                return result;
            }

            private JArray Array(int depth, string close)
            {
                _pos++;
                var result = new JArray();
                while (!Consume(close))
                {
                    if (Consume(","))
                        continue;
                    if (Peek() == '\0')
                        throw new FormatException("unterminated array");
                    result.Add(Value(depth + 1));
                }
                return result;
            }

            private string ReadKey()
            {
                var match = KeyRegex.Match(_text, _pos);
                if (!match.Success)
                    throw new FormatException("object key expected");
                _pos += match.Length;
                return match.Value;
            }

            /// <summary>A same-line <c>&lt;placeholder&gt;</c>.</summary>
            private bool SkipAnglePlaceholder()
            {
                if (_text[_pos] != '<')
                    return false;
                int close = _text.IndexOf('>', _pos);
                int lineEnd = _text.IndexOf('\n', _pos);
                if (close < 0 || (lineEnd >= 0 && lineEnd < close))
                    return false;
                _pos = close + 1;
                return true;
            }

            /// <summary>After json.dumps(value: skips further arguments (indent=2, ...) through the closing parenthesis.</summary>
            private void SkipRestOfCall()
            {
                while (!Consume(")"))
                {
                    char c = Peek();
                    if (c == '\0' || c == '}' || c == ']')
                        throw new FormatException("unterminated call");
                    if (!Consume(","))
                        SkipToDelimiter();
                }
            }

            private void SkipToDelimiter()
            {
                for (char c = Peek(); !IsDelimiter(c); c = Peek())
                {
                    if (c == '\0')
                        throw new FormatException("unexpected end");
                    if (AtString())
                        ReadString();
                    else if (c == '{' || c == '[' || c == '(')
                        SkipBalanced();
                    else
                        _pos++;
                }
            }

            private void SkipBalanced()
            {
                int depth = 0;
                for (char c = Peek(); c != '\0'; c = Peek())
                {
                    if (AtString())
                    {
                        ReadString();
                        continue;
                    }

                    _pos++;
                    if (c == '{' || c == '[' || c == '(')
                        depth++;
                    else if ((c == '}' || c == ']' || c == ')') && --depth == 0)
                        return;
                }
                throw new FormatException("unbalanced brackets");
            }

            private static bool IsDelimiter(char c) => c == ',' || c == '}' || c == ']' || c == ')';

            private char Peek()
            {
                SkipTrivia();
                return _pos < _text.Length ? _text[_pos] : '\0';
            }

            /// <summary>Whitespace and Python comments.</summary>
            private void SkipTrivia()
            {
                while (_pos < _text.Length)
                {
                    if (char.IsWhiteSpace(_text[_pos]))
                        _pos++;
                    else if (_text[_pos] == '#')
                    {
                        int lineEnd = _text.IndexOf('\n', _pos);
                        _pos = lineEnd < 0 ? _text.Length : lineEnd;
                    }
                    else
                    {
                        return;
                    }
                }
            }

            private bool Consume(string token)
            {
                SkipTrivia();
                if (string.CompareOrdinal(_text, _pos, token, 0, token.Length) != 0)
                    return false;
                _pos += token.Length;
                return true;
            }

            /// <summary>A quote, optionally behind a Python string prefix (f, r, b, u, rb, ...).</summary>
            private bool AtString()
            {
                int p = _pos;
                while (p < _text.Length && p - _pos < 2 && StringPrefixes.IndexOf(_text[p]) >= 0)
                    p++;
                return p < _text.Length && (_text[p] == '"' || _text[p] == '\'');
            }

            private string ReadString()
            {
                int p = _pos;
                while (StringPrefixes.IndexOf(_text[p]) >= 0)
                    p++;
                char quote = _text[p];
                var triple = new string(quote, 3);
                if (string.CompareOrdinal(_text, p, triple, 0, 3) == 0)
                {
                    int close = _text.IndexOf(triple, p + 3, StringComparison.Ordinal);
                    if (close < 0)
                        throw new FormatException("unterminated string");
                    _pos = close + 3;
                    return _text.Substring(p + 3, close - p - 3);
                }

                var builder = new StringBuilder();
                for (int i = p + 1; i < _text.Length && _text[i] != '\n'; i++)
                {
                    if (_text[i] == quote)
                    {
                        _pos = i + 1;
                        return builder.ToString();
                    }
                    builder.Append(_text[i] == '\\' && i + 1 < _text.Length ? _text[++i] : _text[i]);
                }
                throw new FormatException("unterminated string");
            }
        }

        /// <summary>
        /// Finds TItem of the BatchExecutor.Execute&lt;TItem&gt; call a batch skill makes, by decoding method IL: the skill
        /// method itself, then methods of its declaring type and of the compiler-generated types nested in it (lambdas,
        /// local functions), a few calls deep. Null when no such call exists (a batch skill not built on BatchExecutor).
        /// </summary>
        private static class BatchItemTypeResolver
        {
            private const int MaxDepth = 4;
            private const int MethodDefTable = 0x06;
            private const int MethodSpecTable = 0x2B;

            private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(field => field.GetValue(null))
                .OfType<OpCode>()
                .GroupBy(op => op.Value)
                .ToDictionary(group => group.Key, group => group.First());

            public static Type Resolve(MethodInfo skillMethod)
            {
                return skillMethod == null ? null : Find(skillMethod, skillMethod.DeclaringType, new HashSet<MethodBase>(), 0);
            }

            private static Type Find(MethodBase method, Type owner, HashSet<MethodBase> visited, int depth)
            {
                if (depth > MaxDepth || !visited.Add(method))
                    return null;

                var callees = ReadCallees(method);
                foreach (var callee in callees)
                {
                    var itemType = ExecuteTypeArgument(callee);
                    if (itemType != null)
                        return itemType;
                }

                foreach (var callee in callees)
                {
                    if (!IsNestedIn(callee, owner))
                        continue;
                    var found = Find(callee, owner, visited, depth + 1);
                    if (found != null)
                        return found;
                }

                return null;
            }

            /// <summary>
            /// Name and declaring type are checked first: a method whose signature names a type from an assembly that
            /// is not loaded throws on DeclaringType, and on Mono touching its generic flags can crash the runtime.
            /// </summary>
            private static Type ExecuteTypeArgument(MethodBase callee)
            {
                try
                {
                    return callee.Name == nameof(BatchExecutor.Execute) && callee.DeclaringType == typeof(BatchExecutor) &&
                           callee is MethodInfo info && info.IsGenericMethod
                        ? info.GetGenericArguments()[0]
                        : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            private static bool IsNestedIn(MethodBase callee, Type owner)
            {
                try
                {
                    for (var current = callee.DeclaringType; current != null; current = current.DeclaringType)
                    {
                        if (current == owner)
                            return true;
                    }
                }
                catch (Exception)
                {
                    // Declared in a type that cannot be loaded: not a helper this lookup can read.
                }
                return false;
            }

            /// <summary>Every method operand (call, callvirt, newobj, ldftn, ...) in the method body, in IL order.</summary>
            private static List<MethodBase> ReadCallees(MethodBase method)
            {
                var callees = new List<MethodBase>();
                byte[] il;
                try
                {
                    il = method.GetMethodBody()?.GetILAsByteArray();
                }
                catch (Exception)
                {
                    return callees;
                }
                if (il == null)
                    return callees;

                var typeArguments = method.DeclaringType != null && method.DeclaringType.IsGenericType
                    ? method.DeclaringType.GetGenericArguments()
                    : null;
                var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

                int pos = 0;
                while (pos < il.Length)
                {
                    short value = il[pos] == 0xFE && pos + 1 < il.Length
                        ? unchecked((short)(0xFE00 | il[pos + 1]))
                        : il[pos];
                    pos += il[pos] == 0xFE ? 2 : 1;
                    if (!OpCodesByValue.TryGetValue(value, out var op))
                        break;

                    if (op.OperandType == OperandType.InlineMethod && pos + 4 <= il.Length)
                    {
                        // Only MethodDef (this module) and MethodSpec (generic instantiations such as Execute<T>) tokens:
                        // MemberRefs point into other assemblies and can never be BatchExecutor or a helper of the
                        // skill type, and Mono can hand back an unusable MethodInfo for one whose assembly is absent.
                        int token = BitConverter.ToInt32(il, pos);
                        int table = (token >> 24) & 0xFF;
                        if (table == MethodDefTable || table == MethodSpecTable)
                        {
                            try
                            {
                                var callee = method.Module.ResolveMethod(token, typeArguments, methodArguments);
                                if (callee != null)
                                    callees.Add(callee);
                            }
                            catch (Exception)
                            {
                                // A token from another generic context: not a call this lookup can follow.
                            }
                        }
                    }

                    pos += OperandSize(op.OperandType, il, pos);
                }

                return callees;
            }

            private static int OperandSize(OperandType type, byte[] il, int pos)
            {
                switch (type)
                {
                    case OperandType.InlineNone:
                        return 0;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        return 1;
                    case OperandType.InlineVar:
                        return 2;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        return 8;
                    case OperandType.InlineSwitch:
                        return pos + 4 <= il.Length ? 4 + 4 * BitConverter.ToInt32(il, pos) : il.Length;
                    default:
                        return 4;
                }
            }
        }
    }
}

// Producer:Betsy
