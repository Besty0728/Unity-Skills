using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// The red line between a skill body's own required-argument guards and the router's <c>required</c> flag.
    ///
    /// <para>A parameter with no default (or a null default) that the body rejects in its opening guards --
    /// <c>Validate.Required(p, ...)</c>, <c>Validate.SafePath(p, ...)</c>, or
    /// <c>if (string.IsNullOrEmpty(p) [|| ...]) return ...</c> -- can never be omitted, yet its CLR signature reads as
    /// optional. Unless <c>RequiredParams</c> (or a literal <c>RequiresInput</c> entry) says so, the schema advertises
    /// it as optional and dryRun calls an empty body valid, and the call fails only once it executes.</para>
    ///
    /// <para>"Opening guards" is the run of statements at the very top of the body made only of such guards, other
    /// <c>Validate.*</c> guards, alias assignments (<c>a = a ?? b;</c>) and an <c>#if</c> package stub
    /// (<c>#if !PKG return Stub(); #else</c>); the first other statement ends the run. A condition joined with
    /// <c>&amp;&amp;</c> is conditional and names nothing, while <c>||</c> names each side. Guards further down depend on
    /// what came before them and are deliberately not scanned: this is a floor, not a completeness claim.</para>
    /// </summary>
    [TestFixture]
    public class RequiredParamsSourceScanTests
    {
        /// <summary>
        /// Guarded parameters that must stay optional: an alias fills them, or the guard applies to one mode of the call only.
        /// </summary>
        private static readonly Dictionary<string, string> Exemptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["find_objects_by_name.nameContains"] = "alias: nameContains = nameContains ?? name",
            ["script_create.scriptName"] = "alias: scriptName = scriptName ?? name",
            ["smart_select_by_component.componentName"] = "alias: componentName = componentName ?? componentType",
            ["cinemachine_set_vcam_property.propertyName"] = "conditional: a lens-only call (fov/nearClip/farClip/orthoSize) names no property",
            ["cinemachine_set_blend.fromCamera"] = "conditional: omitting both cameras sets the brain's default blend",
            ["cinemachine_set_blend.toCamera"] = "conditional: omitting both cameras sets the brain's default blend",
            ["behavior_blackboard_list.graphAssetPath"] = "conditional: a GameObject locator (name/instanceId/path) replaces it",
        };

        // Guards that make their parameter required. Other Validate.* calls (InRange, RequiredJsonArray, ...) keep the
        // opening run going but say nothing about presence.
        private static readonly HashSet<string> PresenceGuards = new HashSet<string>(StringComparer.Ordinal)
        {
            "Required", "SafePath", "IsNullOrEmpty", "IsNullOrWhiteSpace"
        };

        /// <summary>
        /// Registered skills the source scan cannot pair with a definition, each with the reason. Empty today: every
        /// registered skill is defined by a [UnitySkill] method under Editor/Skills.
        /// </summary>
        private static readonly Dictionary<string, string> KnownUnmatchedSkills = new Dictionary<string, string>(StringComparer.Ordinal)
        {
        };

        private SurfaceProfileKind _savedProfile;

        [SetUp]
        public void SetUp()
        {
            // The schema enumerates visible skills only; every assertion here is about the registry as a whole.
            _savedProfile = SkillsSurfaceProfile.Current;
            SkillsSurfaceProfile.Current = SurfaceProfileKind.Full;
        }

        [TearDown]
        public void TearDown() => SkillsSurfaceProfile.Current = _savedProfile;

        [Test]
        public void GuardedParameters_AreRequiredInSchema()
        {
            var scan = ScanRegistry();
            var schema = SchemaRequiredFlags();

            var issues = scan.MustBeRequired
                .Where(pair => !schema.TryGetValue(pair.Key, out var required) || !required)
                .Select(pair => $"{pair.Key} (opening guard: {pair.Value})")
                .ToArray();

            Assert.That(issues, Is.Empty,
                $"{issues.Length} parameter(s) are rejected by their skill's opening guard when omitted, yet the schema " +
                "reports them optional, so dryRun calls a body without them valid. Declare them in RequiredParams, or " +
                "add a commented entry to Exemptions if the guard is really conditional:\n" + string.Join("\n", issues));
        }

        [Test]
        public void ExemptAndDefaultedGuardedParameters_StayOptional()
        {
            var scan = ScanRegistry();
            var schema = SchemaRequiredFlags();

            var issues = scan.MustStayOptional
                .Where(pair => schema.TryGetValue(pair.Key, out var required) && required)
                .Select(pair => $"{pair.Key} ({pair.Value})")
                .ToArray();

            Assert.That(issues, Is.Empty,
                "These parameters have a usable default or an alternative, so marking them required would reject calls " +
                "that work today:\n" + string.Join("\n", issues));
        }

        [Test]
        public void Exemptions_NameRealSkillParameters()
        {
            var stale = new List<string>();
            foreach (var key in Exemptions.Keys)
            {
                var dot = key.IndexOf('.');
                var skillName = key.Substring(0, dot);
                var parameterName = key.Substring(dot + 1);
                if (!SkillRouter.TryGetSkill(skillName, out var skill))
                    continue;
                if (skill.Parameters.All(p => p.Name != parameterName))
                    stale.Add(key);
            }

            Assert.That(stale, Is.Empty,
                "Exemptions name parameters that no longer exist; remove them so the list keeps meaning something: " +
                string.Join(", ", stale));
        }

        [Test]
        public void RequiredParams_NameDeclaredParameters()
        {
            var issues = new List<string>();
            foreach (var skill in SkillRouter.GetAllSkillsSnapshotUnfiltered())
            {
                if (skill.RequiredParams == null)
                    continue;
                foreach (var name in skill.RequiredParams)
                {
                    if (string.IsNullOrWhiteSpace(name) ||
                        !skill.Parameters.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        issues.Add($"{skill.Name}: '{name}'");
                }
            }

            Assert.That(issues, Is.Empty,
                "RequiredParams entries must be literal parameter names -- anything else makes nothing required: " +
                string.Join(", ", issues));
        }

        [Test]
        public void Scan_CoversTheRegistry()
        {
            var scan = ScanRegistry();

            // An explicit list instead of a ratio floor: a ratio let up to a tenth of the registry drop out of every
            // guard check without anything going red.
            var unexpected = scan.UnmatchedSkills.Where(name => !KnownUnmatchedSkills.ContainsKey(name)).ToArray();
            var stale = KnownUnmatchedSkills.Keys.Where(name => !scan.UnmatchedSkills.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.That(unexpected, Is.Empty,
                $"{unexpected.Length} registered skill(s) have no [UnitySkill] definition the scanner can find " +
                $"({scan.DefinitionsParsed} definitions parsed, {scan.SkillsMatched} skills matched), so every guard " +
                "check silently skips them. Fix the scanner, or list them in KnownUnmatchedSkills with the reason:\n" +
                string.Join("\n", unexpected));
            Assert.That(stale, Is.Empty,
                "KnownUnmatchedSkills entries the scanner now finds; remove them so the list keeps meaning something: " +
                string.Join(", ", stale));
            // About 230 guarded no-default parameters exist today; far fewer means the guard parser stopped recognizing them.
            Assert.That(scan.GuardedPairs, Is.GreaterThanOrEqualTo(150),
                $"Only {scan.GuardedPairs} guarded parameters found -- the opening-guard parser is likely broken.");
        }

        /// <summary>
        /// The canary for the scanner itself: a synthetic skill whose guards are known, including the two historical
        /// traps -- a guard after the opening run must not count, and a comment holding commas inside the attribute
        /// must not shift the named fields that follow it.
        /// </summary>
        [Test]
        public void SourceScanner_ReadsASyntheticSkill()
        {
            const string raw =
                "public static class CanarySkills\n" +
                "{\n" +
                "    [UnitySkill(\"canary_probe\", \"A probe, with commas.\",\n" +
                "        Category = SkillCategory.Debug, // a comment, with, commas\n" +
                "        TracksWorkflow = true, RiskLevel = \"high\")]\n" +
                "    public static object CanaryProbe(string target = null, string mode = null, string late = null)\n" +
                "    {\n" +
                "        if (Validate.Required(target, \"target\") is object err) return err;\n" +
                "        if (string.IsNullOrEmpty(mode)) return null;\n" +
                "        DoWork();\n" +
                "        if (string.IsNullOrEmpty(late)) return null;\n" +
                "        return null;\n" +
                "    }\n" +
                "}\n";

            var definitions = SourceScanner.FindSkillDefinitions(raw, SourceScanner.Mask(raw)).ToList();
            Assert.That(definitions.Select(d => d.Name), Is.EqualTo(new[] { "canary_probe" }));

            var guards = SourceScanner.OpeningGuards(definitions[0].Body);
            Assert.That(guards, Is.EquivalentTo(new[] { ("target", "Required"), ("mode", "IsNullOrEmpty") }),
                "Opening guards name target and mode; the guard after DoWork() is outside the opening run.");

            var span = SourceScanner.FindSkillAttributeSpans(raw, SourceScanner.Mask(raw)).Single();
            var fields = SourceScanner.ParseNamedFields(span.AttributeText);
            Assert.That(fields["TracksWorkflow"], Is.EqualTo("true"), "A comment with commas must not shift later fields.");
            Assert.That(fields["RiskLevel"], Is.EqualTo("\"high\""));
            Assert.That(fields["Category"], Is.EqualTo("SkillCategory.Debug"));
        }

        /// <summary>
        /// The URP-family <c>#if PKG ... #else ... #endif</c> modules (Decal/PostProcess/URP/Volume) each declare
        /// every skill twice: a real branch that compiles in when the render pipeline package is installed, and a
        /// stub branch (<c>RenderPipelineSkillsCommon.NoSRP()</c>/<c>NoURP()</c>) that compiles in when it isn't.
        /// Only one branch is ever in the running binary, so a caller can never diff the two - which is exactly
        /// why metadata drift between them is invisible at runtime: the 2026-09 audit found all 33 pairs had let
        /// their stub branch's declarative fields (most commonly <c>RequiresPackages</c> itself, and
        /// <c>TracksWorkflow</c>/<c>Mutates*</c>/<c>RequiresInput</c> on writers) silently fall behind the real
        /// branch's, sometimes for years. The method signature and description are not compared here: a source
        /// scan already confirmed those stay byte-for-byte identical in every pair (a second, independent
        /// doc-consistency mechanism would catch a signature drift the moment it made the stub's parameter list
        /// disagree with the shipped docs).
        /// </summary>
        [Test]
        public void StubBranchMetadata_MatchesRealBranch()
        {
            var comparedFields = new[]
            {
                "Category", "Operation", "Tags", "Outputs", "RequiresInput", "RequiredParams",
                "RequiresPackages", "TracksWorkflow", "MutatesScene", "MutatesAssets",
                "MayTriggerReload", "MayEnterPlayMode", "ReadOnly", "Mode", "RiskLevel", "SkipAutoPresnapshot"
            };

            var root = GetSkillsSourceRoot();
            var offenders = new List<string>();
            var pairsChecked = 0;

            foreach (var path in Directory.GetFiles(root, "*.cs").OrderBy(p => p, StringComparer.Ordinal))
            {
                var raw = File.ReadAllText(path);
                var masked = SourceScanner.Mask(raw);

                var occurrencesByName = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
                foreach (var span in SourceScanner.FindSkillAttributeSpans(raw, masked))
                {
                    if (!occurrencesByName.TryGetValue(span.Name, out var list))
                        occurrencesByName[span.Name] = list = new List<Dictionary<string, string>>();
                    list.Add(SourceScanner.ParseNamedFields(span.AttributeText));
                }

                // A skill name declared more than once in the same file is exactly the #if/#else stub-pair
                // shape (confirmed: today that is only the 33 URP-family pairs, and only in these four files);
                // anything declared once has no second branch to drift from and is skipped.
                foreach (var group in occurrencesByName.Where(pair => pair.Value.Count > 1))
                {
                    pairsChecked++;
                    foreach (var field in comparedFields)
                    {
                        var values = group.Value
                            .Select(occurrence => occurrence.TryGetValue(field, out var value) ? value : null)
                            .Distinct()
                            .ToArray();
                        if (values.Length > 1)
                        {
                            offenders.Add($"{Path.GetFileName(path)}::{group.Key}.{field}: " +
                                          string.Join(" vs ", values.Select(value => value ?? "(not set)")));
                        }
                    }
                }
            }

            Assert.That(pairsChecked, Is.GreaterThanOrEqualTo(33),
                $"Only found {pairsChecked} skill(s) declared more than once in the same file; expected at least " +
                "the 33 known #if/#else URP-family stub pairs (Decal/PostProcess/URP/Volume) - the attribute-span " +
                "scanner may be broken, which would make a green result here meaningless.");

            Assert.That(offenders, Is.Empty,
                $"{offenders.Count} declarative metadata field(s) differ between a skill's #if stub branch and " +
                "its real branch. Both branches are the same endpoint - only one ever compiles in, depending on " +
                "whether the render pipeline package is installed - so a caller must see identical capability / " +
                "impact metadata regardless of which one ships:\n" + string.Join("\n", offenders));
        }

        /// <summary>RequiredParams flows through the router's one required check: schema flag, dryRun parameters, MissingParams.</summary>
        [Test]
        public void RequiredParams_MakeADefaultedParameterRequired_WithoutDoubleReporting()
        {
            Assume.That(SkillRouter.TryGetSkill("component_add", out var real), Is.True, "component_add is not registered.");
            var probe = CloneWith(real, clone => clone.RequiredParams = new[] { "componentType" });

            var missing = SkillRouter.ValidateParameters(probe, "{\"name\":\"__required_params_probe__\"}");
            Assert.That(missing.MissingParams, Does.Contain("componentType"));
            Assert.That(missing.Valid, Is.False);

            var detail = missing.ParameterDetails.Select(entry => JObject.FromObject(entry))
                .Single(entry => (string)entry["name"] == "componentType");
            Assert.That(detail["required"]?.Value<bool>(), Is.True, "dryRun's parameter list must agree with MissingParams.");

            var semanticFields = missing.SemanticErrors.Select(error => JObject.FromObject(error))
                .Select(error => (string)error["field"]).ToArray();
            Assert.That(semanticFields, Has.None.EqualTo("componentType"),
                "A parameter already listed in MissingParams must not come back a second time as a semantic error.");

            var provided = SkillRouter.ValidateParameters(probe,
                "{\"name\":\"__required_params_probe__\",\"componentType\":\"BoxCollider\"}");
            Assert.That(provided.MissingParams, Is.Empty);
        }

        // ============================================================
        // Registry-side view
        // ============================================================

        private sealed class ScanResult
        {
            public int DefinitionsParsed;
            public int SkillsMatched;
            public int GuardedPairs;
            public readonly SortedSet<string> UnmatchedSkills = new SortedSet<string>(StringComparer.Ordinal);
            public readonly SortedDictionary<string, string> MustBeRequired = new SortedDictionary<string, string>(StringComparer.Ordinal);
            public readonly SortedDictionary<string, string> MustStayOptional = new SortedDictionary<string, string>(StringComparer.Ordinal);
        }

        private static ScanResult ScanRegistry()
        {
            var root = GetSkillsSourceRoot();
            Assert.That(Directory.Exists(root), Is.True, $"Skill source directory not found: {root}");

            var result = new ScanResult();
            // A #if/#else pair declares one skill twice (real body + package stub); their guards are unioned.
            var guardsBySkill = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var path in Directory.GetFiles(root, "*.cs").OrderBy(p => p, StringComparer.Ordinal))
            {
                var raw = File.ReadAllText(path);
                var masked = SourceScanner.Mask(raw);
                foreach (var definition in SourceScanner.FindSkillDefinitions(raw, masked))
                {
                    result.DefinitionsParsed++;
                    if (!guardsBySkill.TryGetValue(definition.Name, out var guards))
                        guardsBySkill[definition.Name] = guards = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (definition.Body == null)
                        continue;
                    foreach (var (parameter, guard) in SourceScanner.OpeningGuards(definition.Body))
                    {
                        if (PresenceGuards.Contains(guard) && !guards.ContainsKey(parameter))
                            guards[parameter] = guard;
                    }
                }
            }

            foreach (var skill in SkillRouter.GetAllSkillsSnapshotUnfiltered())
            {
                if (!guardsBySkill.TryGetValue(skill.Name, out var guards))
                {
                    result.UnmatchedSkills.Add(skill.Name);
                    continue;
                }
                result.SkillsMatched++;

                foreach (var pair in guards)
                {
                    var parameter = skill.Parameters.FirstOrDefault(p => p.Name == pair.Key);
                    if (parameter == null)
                        continue; // guards a local, not a parameter

                    var key = skill.Name + "." + parameter.Name;
                    if (parameter.HasDefaultValue && parameter.DefaultValue != null)
                    {
                        result.MustStayOptional[key] = $"{pair.Value} guard, default {parameter.DefaultValue}";
                        continue;
                    }

                    result.GuardedPairs++;
                    if (!Exemptions.ContainsKey(key))
                        result.MustBeRequired[key] = pair.Value;
                }
            }

            foreach (var exemption in Exemptions)
                result.MustStayOptional[exemption.Key] = "exempt, " + exemption.Value;

            return result;
        }

        /// <summary>"skill.parameter" -> required, read from the v1 schema the wire actually carries.</summary>
        private static Dictionary<string, bool> SchemaRequiredFlags()
        {
            var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
            var schema = JObject.Parse(SkillRouter.GetSchema());
            foreach (var skill in (JArray)schema["skills"])
            {
                var skillName = (string)skill["name"];
                foreach (var parameter in (skill["parameters"] as JArray) ?? new JArray())
                    flags[skillName + "." + (string)parameter["name"]] = parameter["required"]?.Value<bool>() == true;
            }
            return flags;
        }

        private static SkillRouter.SkillInfo CloneWith(SkillRouter.SkillInfo source, Action<SkillRouter.SkillInfo> change)
        {
            var clone = new SkillRouter.SkillInfo();
            foreach (var field in typeof(SkillRouter.SkillInfo).GetFields(BindingFlags.Public | BindingFlags.Instance))
                field.SetValue(clone, field.GetValue(source));
            change(clone);
            return clone;
        }

        /// <summary>The same dual-path resolution as OutputsReturnContractTests.GetSkillsSourceRoot: in-project first, then the package cache.</summary>
        private static string GetSkillsSourceRoot()
        {
            var projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot != null)
            {
                var inProject = Path.Combine(projectRoot.FullName, "SkillsForUnity", "Editor", "Skills");
                if (Directory.Exists(inProject))
                    return inProject;
            }

            var packageInfo = PackageInfo.FindForAssembly(typeof(UnitySkillAttribute).Assembly)
                              ?? PackageInfo.FindForAssembly(typeof(RequiredParamsSourceScanTests).Assembly);
            if (packageInfo != null)
            {
                var inPackage = Path.Combine(packageInfo.resolvedPath, "Editor", "Skills");
                if (Directory.Exists(inPackage))
                    return inPackage;
            }

            return projectRoot != null
                ? Path.Combine(projectRoot.FullName, "SkillsForUnity", "Editor", "Skills")
                : "SkillsForUnity/Editor/Skills";
        }

        // ============================================================
        // Source-side view: a lexical scanner, just enough to read opening guards
        // ============================================================

        private static class SourceScanner
        {
            public sealed class SkillDefinition
            {
                public string Name;
                // Masked body including its braces; null for an expression-bodied method, which has no guards to read.
                public string Body;
            }

            private static readonly Regex AttributeStart = new Regex(@"\[\s*UnitySkill\s*\(", RegexOptions.Compiled);
            private static readonly Regex MethodHead = new Regex(@"public\s+static\s+[\w<>\[\],\s\.\?]+?\s+(\w+)\s*\(", RegexOptions.Compiled);
            private static readonly Regex IfHead = new Regex(@"\Gif\s*\(", RegexOptions.Compiled);
            private static readonly Regex ValidateCall = new Regex(@"^Validate\s*\.\s*(\w+)\s*(?:<[^>]*>)?\s*\(\s*([A-Za-z_]\w*)", RegexOptions.Compiled);
            private static readonly Regex NullOrEmptyCheck = new Regex(@"^string\s*\.\s*(IsNullOrEmpty|IsNullOrWhiteSpace)\s*\(\s*([A-Za-z_]\w*)\s*\)$", RegexOptions.Compiled);
            private static readonly Regex AliasAssignment = new Regex(@"^([A-Za-z_]\w*)\s*=\s*\1\s*\?\?\s*[A-Za-z_]\w*\s*;$", RegexOptions.Compiled);
            private static readonly Regex ValidateLocal = new Regex(@"^(?:var|object)\s+([A-Za-z_]\w*)\s*=\s*Validate\s*\.\s*(\w+)\s*\(\s*([A-Za-z_]\w*)", RegexOptions.Compiled);
            private static readonly Regex NotNullCheck = new Regex(@"^([A-Za-z_]\w*)\s*!=\s*null$", RegexOptions.Compiled);
            private static readonly Regex StubBranchEnd = new Regex(@"^#(else|elif|endif)\b", RegexOptions.Compiled);

            /// <summary>
            /// Blanks comments and the contents of string/char literals (interpolation holes included), keeping every
            /// literal's quotes and every position, so brackets and keywords can be matched safely and a literal's text
            /// can still be read back from the raw source at the same offsets.
            /// </summary>
            public static string Mask(string src)
            {
                var buffer = src.ToCharArray();
                int n = src.Length;
                int i = 0;
                while (i < n)
                {
                    char c = src[i];
                    if (c == '/' && i + 1 < n && src[i + 1] == '/')
                    {
                        int end = src.IndexOf('\n', i);
                        i = Blank(buffer, i, end < 0 ? n : end);
                        continue;
                    }
                    if (c == '/' && i + 1 < n && src[i + 1] == '*')
                    {
                        int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        i = Blank(buffer, i, end < 0 ? n : end + 2);
                        continue;
                    }
                    if (c == '\'')
                    {
                        int end = CharEnd(src, i);
                        Blank(buffer, i + 1, end - 1);
                        i = end;
                        continue;
                    }
                    if (c == '"' || ((c == '$' || c == '@') && IsStringStart(src, i)))
                    {
                        int quote = i;
                        while (src[quote] != '"') quote++;
                        int end = StringEnd(src, i);
                        Blank(buffer, quote + 1, end - 1);
                        i = end;
                        continue;
                    }
                    i++;
                }
                return new string(buffer);
            }

            public static IEnumerable<SkillDefinition> FindSkillDefinitions(string raw, string masked)
            {
                foreach (Match attribute in AttributeStart.Matches(masked))
                {
                    int open = attribute.Index + attribute.Length - 1;
                    int close = MatchBracket(masked, open, '(', ')');
                    if (close < 0)
                        continue;

                    int quote = masked.IndexOf('"', open, close - open);
                    int endQuote = quote < 0 ? -1 : masked.IndexOf('"', quote + 1, close - quote - 1);
                    if (endQuote < 0)
                        continue;

                    var head = MethodHead.Match(masked, close);
                    if (!head.Success)
                        continue;
                    int paramClose = MatchBracket(masked, head.Index + head.Length - 1, '(', ')');
                    if (paramClose < 0)
                        continue;

                    string body = null;
                    int next = SkipWhitespace(masked, paramClose + 1, masked.Length);
                    if (next < masked.Length && masked[next] == '{')
                    {
                        int bodyClose = MatchBracket(masked, next, '{', '}');
                        if (bodyClose < 0)
                            continue;
                        body = masked.Substring(next, bodyClose - next + 1);
                    }

                    yield return new SkillDefinition { Name = raw.Substring(quote + 1, endQuote - quote - 1), Body = body };
                }
            }

            /// <summary>One [UnitySkill(...)] attribute's raw (unmasked) argument text, keyed by the skill name
            /// pulled from its first string literal. <see cref="StubBranchMetadataParityTests"/> below has no use
            /// for the method body <see cref="FindSkillDefinitions"/> reads, only the attribute's own fields.</summary>
            public sealed class SkillAttributeSpan
            {
                public string Name;
                public string AttributeText;
            }

            /// <summary>
            /// A second orchestrating walk over the same AttributeStart / MethodHead / MatchBracket primitives
            /// <see cref="FindSkillDefinitions"/> already uses - not a second lexer - that keeps the attribute's
            /// argument text instead of the method body. <see cref="FindSkillDefinitions"/> is left untouched:
            /// <c>ScanRegistry</c> depends on its exact signature and behaviour.
            /// </summary>
            public static IEnumerable<SkillAttributeSpan> FindSkillAttributeSpans(string raw, string masked)
            {
                foreach (Match attribute in AttributeStart.Matches(masked))
                {
                    int open = attribute.Index + attribute.Length - 1;
                    int close = MatchBracket(masked, open, '(', ')');
                    if (close < 0)
                        continue;

                    int quote = masked.IndexOf('"', open, close - open);
                    int endQuote = quote < 0 ? -1 : masked.IndexOf('"', quote + 1, close - quote - 1);
                    if (endQuote < 0)
                        continue;

                    // Same follow-a-method-head requirement as FindSkillDefinitions, so both walks agree on
                    // which attributes count as skill definitions (and skip e.g. doc-comment mentions).
                    if (!MethodHead.Match(masked, close).Success)
                        continue;

                    yield return new SkillAttributeSpan
                    {
                        Name = raw.Substring(quote + 1, endQuote - quote - 1),
                        AttributeText = raw.Substring(open + 1, close - open - 1)
                    };
                }
            }

            private static readonly Regex NamedField = new Regex(@"^\s*([A-Za-z_]\w*)\s*=(?!=)\s*", RegexOptions.Compiled);

            /// <summary>
            /// The top-level (bracket-depth 0) comma-separated field assignments in an attribute's argument text,
            /// keyed by field name. The two positional arguments (name and description string literals) have no
            /// leading "word =" and are silently skipped, exactly like every other malformed segment.
            /// </summary>
            public static Dictionary<string, string> ParseNamedFields(string attributeText)
            {
                var masked = Mask(attributeText);
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                int depth = 0, start = 0;
                for (int i = 0; i <= masked.Length; i++)
                {
                    bool atEnd = i == masked.Length;
                    char c = atEnd ? ',' : masked[i];
                    if (!atEnd)
                    {
                        if (c == '(' || c == '[' || c == '{') { depth++; continue; }
                        if (c == ')' || c == ']' || c == '}') { depth--; continue; }
                    }
                    if (depth == 0 && c == ',')
                    {
                        var segment = masked.Substring(start, i - start);
                        var match = NamedField.Match(segment);
                        if (match.Success)
                        {
                            var value = attributeText.Substring(start + match.Length, segment.Length - match.Length).Trim();
                            fields[match.Groups[1].Value] = Regex.Replace(value, @"\s+", " ");
                        }
                        start = i + 1;
                    }
                }
                return fields;
            }

            /// <summary>The (parameter, guard kind) pairs named by a masked body's opening guards.</summary>
            public static List<(string parameter, string guard)> OpeningGuards(string body)
            {
                var guards = new List<(string parameter, string guard)>();
                var guardLocals = new Dictionary<string, (string parameter, string guard)>(StringComparer.Ordinal);
                var statements = TopLevelStatements(body);
                bool afterIfDirective = false;

                for (int s = 0; s < statements.Count; s++)
                {
                    var statement = statements[s];
                    if (statement.Kind == StatementKind.Directive)
                    {
                        afterIfDirective = statement.Text.StartsWith("#if", StringComparison.Ordinal) ||
                                           statement.Text.StartsWith("#elif", StringComparison.Ordinal);
                        continue;
                    }

                    // "#if !PKG  return Stub();  #else": the real body, and its guards, start after the stub.
                    bool stubReturn = afterIfDirective && statement.Kind == StatementKind.Other &&
                                      StartsWithKeyword(statement.Text, "return") &&
                                      s + 1 < statements.Count && statements[s + 1].Kind == StatementKind.Directive &&
                                      StubBranchEnd.IsMatch(statements[s + 1].Text);
                    afterIfDirective = false;
                    if (stubReturn)
                        continue;

                    if (statement.Kind == StatementKind.Other)
                    {
                        if (AliasAssignment.IsMatch(statement.Text))
                            continue;
                        var local = ValidateLocal.Match(statement.Text);
                        if (!local.Success)
                            break;
                        guardLocals[local.Groups[1].Value] = (local.Groups[3].Value, local.Groups[2].Value);
                        continue;
                    }

                    if (statement.Kind != StatementKind.If || statement.HasElse || !ReturnsImmediately(statement.Then))
                        break;

                    var condition = statement.Text.Trim();
                    if (condition.Contains("&&"))
                    {
                        // Conditional: names no parameter, but a Validate / null-check guard keeps the opening run going.
                        if (condition.Contains("Validate") || condition.Contains("IsNullOr"))
                            continue;
                        break;
                    }

                    var named = new List<(string parameter, string guard)>();
                    bool allGuards = true;
                    foreach (var rawPart in condition.Split(new[] { "||" }, StringSplitOptions.None))
                    {
                        var part = rawPart.Trim();
                        var call = ValidateCall.Match(part);
                        if (call.Success)
                        {
                            named.Add((call.Groups[2].Value, call.Groups[1].Value));
                            continue;
                        }
                        var check = NullOrEmptyCheck.Match(part);
                        if (check.Success)
                        {
                            named.Add((check.Groups[2].Value, check.Groups[1].Value));
                            continue;
                        }
                        var notNull = NotNullCheck.Match(part);
                        if (notNull.Success && guardLocals.TryGetValue(notNull.Groups[1].Value, out var viaLocal))
                        {
                            named.Add(viaLocal);
                            continue;
                        }
                        allGuards = false;
                        break;
                    }

                    if (!allGuards)
                        break;
                    guards.AddRange(named);
                }

                return guards;
            }

            private enum StatementKind { Directive, Block, If, Other }

            private struct Statement
            {
                public StatementKind Kind;
                public string Text;    // Directive / Other: the statement; If: the condition
                public string Then;    // If: the then-branch
                public bool HasElse;
            }

            /// <summary>
            /// The top-level statements of a masked body, stopping after the first block or if/else: the opening-guard run
            /// ends at either, so nothing past them is ever read.
            /// </summary>
            private static List<Statement> TopLevelStatements(string body)
            {
                var statements = new List<Statement>();
                int i = 1;
                int n = body.Length - 1;
                while (i < n)
                {
                    i = SkipWhitespace(body, i, n);
                    if (i >= n)
                        break;

                    if (body[i] == '#')
                    {
                        int end = body.IndexOf('\n', i);
                        if (end < 0 || end > n)
                            end = n;
                        statements.Add(new Statement { Kind = StatementKind.Directive, Text = body.Substring(i, end - i).Trim() });
                        i = end;
                        continue;
                    }

                    if (body[i] == '{')
                    {
                        statements.Add(new Statement { Kind = StatementKind.Block });
                        break;
                    }

                    var ifHead = IfHead.Match(body, i);
                    if (ifHead.Success)
                    {
                        int conditionOpen = ifHead.Index + ifHead.Length - 1;
                        int conditionClose = MatchBracket(body, conditionOpen, '(', ')');
                        if (conditionClose < 0)
                            break;
                        int thenStart = SkipWhitespace(body, conditionClose + 1, n);
                        int thenEnd = thenStart < n && body[thenStart] == '{'
                            ? MatchBracket(body, thenStart, '{', '}')
                            : StatementEnd(body, thenStart, n);
                        if (thenEnd < 0)
                            break;

                        var statement = new Statement
                        {
                            Kind = StatementKind.If,
                            Text = body.Substring(conditionOpen + 1, conditionClose - conditionOpen - 1),
                            Then = body.Substring(thenStart, thenEnd - thenStart + 1),
                        };
                        i = thenEnd + 1;
                        statement.HasElse = StartsWithKeyword(body.Substring(SkipWhitespace(body, i, n)), "else");
                        statements.Add(statement);
                        if (statement.HasElse)
                            break;
                        continue;
                    }

                    int stmtEnd = StatementEnd(body, i, n);
                    statements.Add(new Statement { Kind = StatementKind.Other, Text = body.Substring(i, stmtEnd - i + 1).Trim() });
                    i = stmtEnd + 1;
                }
                return statements;
            }

            private static bool ReturnsImmediately(string then)
            {
                var text = then.Trim();
                if (text.StartsWith("{", StringComparison.Ordinal))
                    text = text.Substring(1).TrimStart();
                return StartsWithKeyword(text, "return");
            }

            private static bool StartsWithKeyword(string text, string keyword) =>
                text.StartsWith(keyword, StringComparison.Ordinal) &&
                (text.Length == keyword.Length || !(char.IsLetterOrDigit(text[keyword.Length]) || text[keyword.Length] == '_'));

            /// <summary>Index of the ';' ending the statement that starts at <paramref name="i"/>, or the last index before <paramref name="limit"/>.</summary>
            private static int StatementEnd(string text, int i, int limit)
            {
                int depth = 0;
                for (; i < limit; i++)
                {
                    char c = text[i];
                    if (c == '(' || c == '{' || c == '[') depth++;
                    else if (c == ')' || c == '}' || c == ']') depth--;
                    else if (c == ';' && depth == 0) return i;
                }
                return limit - 1;
            }

            private static int SkipWhitespace(string text, int i, int limit)
            {
                while (i < limit && char.IsWhiteSpace(text[i]))
                    i++;
                return i;
            }

            private static int MatchBracket(string text, int openIndex, char open, char close)
            {
                int depth = 0;
                for (int i = openIndex; i < text.Length; i++)
                {
                    if (text[i] == open) depth++;
                    else if (text[i] == close && --depth == 0) return i;
                }
                return -1;
            }

            private static int Blank(char[] buffer, int from, int to)
            {
                for (int k = Math.Max(from, 0); k < to && k < buffer.Length; k++)
                {
                    if (buffer[k] != '\n')
                        buffer[k] = ' ';
                }
                return to;
            }

            private static bool IsStringStart(string src, int i)
            {
                int j = i;
                while (j < src.Length && (src[j] == '$' || src[j] == '@'))
                    j++;
                return j > i && j < src.Length && src[j] == '"';
            }

            /// <summary>Index just past the string literal starting at <paramref name="i"/> (which may point at a $ or @ prefix).</summary>
            private static int StringEnd(string src, int i)
            {
                bool verbatim = false;
                bool interpolated = false;
                while (i < src.Length && (src[i] == '$' || src[i] == '@'))
                {
                    if (src[i] == '@') verbatim = true;
                    else interpolated = true;
                    i++;
                }

                int j = i + 1;
                while (j < src.Length)
                {
                    char c = src[j];
                    if (verbatim)
                    {
                        if (c == '"')
                        {
                            if (j + 1 < src.Length && src[j + 1] == '"') { j += 2; continue; }
                            return j + 1;
                        }
                    }
                    else
                    {
                        if (c == '\\') { j += 2; continue; }
                        if (c == '"') return j + 1;
                        if (c == '\n') return j;
                    }

                    if (interpolated && c == '{')
                    {
                        if (j + 1 < src.Length && src[j + 1] == '{') { j += 2; continue; }
                        j = HoleEnd(src, j + 1);
                        continue;
                    }
                    j++;
                }
                return src.Length;
            }

            /// <summary>Index just past the '}' closing an interpolation hole whose code starts at <paramref name="j"/>.</summary>
            private static int HoleEnd(string src, int j)
            {
                int depth = 0;
                while (j < src.Length)
                {
                    char c = src[j];
                    if (c == '"' || ((c == '$' || c == '@') && IsStringStart(src, j))) { j = StringEnd(src, j); continue; }
                    if (c == '\'') { j = CharEnd(src, j); continue; }
                    if (c == '{') depth++;
                    else if (c == '}')
                    {
                        if (depth == 0) return j + 1;
                        depth--;
                    }
                    j++;
                }
                return src.Length;
            }

            private static int CharEnd(string src, int i)
            {
                int j = i + 1;
                while (j < src.Length && src[j] != '\'' && src[j] != '\n')
                    j += src[j] == '\\' ? 2 : 1;
                return Math.Min(j + 1, src.Length);
            }
        }
    }
}

// Producer:Betsy
