using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnitySkills.Internal;

namespace UnitySkills.Tests.Core
{
    /// <summary>
    /// Unity's AssetImportWorker processes load the editor assemblies against the same project and Library folder, so
    /// every editor entry point of this package runs there too. Each one must consult <see cref="ImportWorkerGate"/>
    /// first. A source scan finds every [InitializeOnLoad] static constructor, [InitializeOnLoadMethod] and
    /// AssetPostprocessor callback, including ones compiled out by a missing optional package. Reflection proves the
    /// scan sees every compiled one, a coverage floor keeps a wrong root from passing by scanning nothing, and canaries
    /// prove each rule fires.
    /// </summary>
    [TestFixture]
    public class ImportWorkerGateTests
    {
        private const string Gate = "ImportWorkerGate.IsWorkerProcess";

        /// <summary>
        /// Entry points that may run in a worker, keyed like <see cref="EntryPoint.Key"/>, each with the reason it must.
        /// None needs to today; an entry point added here has to say why a worker cannot do without it.
        /// </summary>
        private static readonly Dictionary<string, string> Allowlist = new Dictionary<string, string>(StringComparer.Ordinal);

        [Test]
        public void EveryEditorEntryPoint_ConsultsTheGate()
        {
            var entryPoints = ScanSources(EditorSources());

            Assert.That(entryPoints.Count, Is.GreaterThanOrEqualTo(30), $"Found only {entryPoints.Count} entry points.");
            Assert.That(entryPoints.Select(e => e.Key), Is.SupersetOf(new[] { "SkillsHttpServer..cctor", "ConsoleLogHistoryService..cctor", "SkillsAuditLog.RegisterLifecycleFlush" }),
                "The scan must see the known entry points.");
            var missingBody = entryPoints.Where(e => e.Body == null).Select(e => e.Where).ToList();
            Assert.That(missingBody, Is.Empty, "Each entry point needs a block body that can return early (an [InitializeOnLoad] type an explicit static constructor).");
            var ungated = entryPoints.Where(e => e.Body != null && !e.Body.Contains(Gate) && !Allowlist.ContainsKey(e.Key)).Select(e => e.Where).ToList();
            Assert.That(ungated, Is.Empty, "Start these with `if (ImportWorkerGate.IsWorkerProcess) return;` or allowlist them with the reason.");
            var stale = Allowlist.Keys.Except(entryPoints.Select(e => e.Key)).ToList();
            Assert.That(stale, Is.Empty, "Allowlisted entry points that no longer exist.");
        }

        [Test]
        public void Scan_SeesEveryCompiledEntryPoint()
        {
            var scanned = new HashSet<string>(ScanSources(EditorSources()).Select(e => e.Key), StringComparer.Ordinal);
            var compiled = CompiledEntryPoints(typeof(ImportWorkerGate).Assembly).ToList();

            Assert.That(compiled.Count, Is.GreaterThanOrEqualTo(25), $"Reflection found only {compiled.Count} entry points.");
            Assert.That(compiled.Where(key => !scanned.Contains(key)), Is.Empty, "The source scan missed compiled entry points.");
        }

        [Test]
        public void Scanner_Canaries()
        {
            const string source = @"
using UnityEditor;
namespace Canary
{
    [InitializeOnLoad]
    internal static class Gated
    {
        static Gated() { if (ImportWorkerGate.IsWorkerProcess) return; Hook(); }
    }

    [UnityEditor.InitializeOnLoad, System.Obsolete(""x"")]
    internal static class Ungated
    {
        static Ungated()
        {
            // ImportWorkerGate.IsWorkerProcess in a comment does not count.
            string s = ""ImportWorkerGate.IsWorkerProcess"";
            Hook();
        }
    }

    internal static class Methods
    {
        [InitializeOnLoadMethod]
        [System.Obsolete(""y"")]
        private static void Interpolated()
        {
            string t = $""{(s.Length > 0 ? ""}"" : ""{"")} {{literal}}"";
            Hook();
        }

        // [InitializeOnLoadMethod] in a comment is not an entry point.
        private static void NotAnEntryPoint() { string u = ""[InitializeOnLoadMethod]""; }

        private sealed class Nested
        {
            [InitializeOnLoadMethod]
            static void GatedNested() { if (ImportWorkerGate.IsWorkerProcess) return; }
        }
    }

    internal sealed class Post : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] a, string[] b, string[] c, string[] d) { Hook(); }
    }

    [InitializeOnLoad]
    internal static class NoConstructor { private static readonly int Value = Hook(); }
}
";
            var entryPoints = ScanSources(new[] { ("Canary.cs", source) }).ToDictionary(e => e.Key);

            CollectionAssert.AreEquivalent(new[]
            {
                "Gated..cctor", "Ungated..cctor", "Methods.Interpolated", "Nested.GatedNested", "Post.OnPostprocessAllAssets", "NoConstructor..cctor",
            }, entryPoints.Keys);
            Assert.That(entryPoints["Gated..cctor"].Body, Does.Contain(Gate));
            Assert.That(entryPoints["Nested.GatedNested"].Body, Does.Contain(Gate));
            Assert.That(entryPoints["Ungated..cctor"].Body, Does.Not.Contain(Gate), "Comments and strings must not satisfy the rule.");
            Assert.That(entryPoints["Methods.Interpolated"].Body, Does.Contain("Hook()"), "Braces inside interpolated strings must not end the body early.");
            Assert.That(entryPoints["Post.OnPostprocessAllAssets"].Body, Does.Not.Contain(Gate));
            Assert.That(entryPoints["NoConstructor..cctor"].Body, Is.Null);
        }

        private sealed class EntryPoint
        {
            public string Key;
            public string Where;
            public string Body;
        }

        private static IEnumerable<(string name, string text)> EditorSources()
        {
            var root = EditorSourceRoot();
            var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            Assert.That(files.Length, Is.GreaterThanOrEqualTo(150), $"Scanned only {files.Length} files under {root}.");
            return files.Select(f => (Path.GetFileName(f), File.ReadAllText(f)));
        }

        private static List<EntryPoint> ScanSources(IEnumerable<(string name, string text)> sources)
        {
            var masked = sources.Select(s => (s.name, code: MaskNonCode(s.text))).ToList();
            var result = new List<EntryPoint>();
            foreach (var (name, code) in masked)
            {
                var classes = ClassSpans(code);

                foreach (Match attribute in Regex.Matches(code, @"\[\s*(?:UnityEditor\.)?InitializeOnLoad(?:Attribute)?\s*[,\]]"))
                {
                    var declaration = Regex.Match(code.Substring(attribute.Index), @"\b(?:class|struct)\s+(\w+)");
                    if (!declaration.Success) continue;
                    string type = declaration.Groups[1].Value;
                    result.Add(new EntryPoint
                    {
                        Key = type + "..cctor",
                        Where = $"{name}: [InitializeOnLoad] {type}",
                        Body = masked.Select(m => StaticConstructorBody(m.code, type)).FirstOrDefault(body => body != null),
                    });
                }

                foreach (Match attribute in Regex.Matches(code, @"\[\s*(?:UnityEditor\.)?InitializeOnLoadMethod(?:Attribute)?\s*[,\]]"))
                {
                    int open = code.IndexOf('(', SkipAttributes(code, attribute.Index));
                    var method = Regex.Match(code.Substring(0, open), @"(\w+)\s*$");
                    string type = EnclosingClass(classes, attribute.Index);
                    result.Add(new EntryPoint
                    {
                        Key = type + "." + method.Groups[1].Value,
                        Where = $"{name}: [InitializeOnLoadMethod] {type}.{method.Groups[1].Value}",
                        Body = BodyAfter(code, open),
                    });
                }

                foreach (Match postprocessor in Regex.Matches(code, @"\bclass\s+(\w+)\s*:\s*(?:UnityEditor\.)?AssetPostprocessor\b"))
                {
                    var span = classes.First(c => c.name == postprocessor.Groups[1].Value && c.open > postprocessor.Index);
                    string classBody = code.Substring(span.open, span.close - span.open);
                    foreach (Match callback in Regex.Matches(classBody, @"\bvoid\s+(On(?:Pre|Post)process\w*|OnAssignMaterialModel)\s*\("))
                    {
                        result.Add(new EntryPoint
                        {
                            Key = span.name + "." + callback.Groups[1].Value,
                            Where = $"{name}: AssetPostprocessor {span.name}.{callback.Groups[1].Value}",
                            Body = BodyAfter(classBody, callback.Index + callback.Length - 1),
                        });
                    }
                }
            }
            return result;
        }

        private static IEnumerable<string> CompiledEntryPoints(Assembly assembly)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var type in types)
            {
                if (type.IsDefined(typeof(InitializeOnLoadAttribute), false))
                    yield return type.Name + "..cctor";
                bool postprocessor = typeof(AssetPostprocessor).IsAssignableFrom(type);
                foreach (var method in type.GetMethods(all))
                {
                    if (method.IsDefined(typeof(InitializeOnLoadMethodAttribute), false))
                        yield return type.Name + "." + method.Name;
                    else if (postprocessor && Regex.IsMatch(method.Name, @"^(On(Pre|Post)process\w*|OnAssignMaterialModel)$"))
                        yield return type.Name + "." + method.Name;
                }
            }
        }

        private static string StaticConstructorBody(string code, string type)
        {
            var constructor = Regex.Match(code, @"\bstatic\s+" + Regex.Escape(type) + @"\s*\(\s*\)");
            return constructor.Success ? BodyAfter(code, constructor.Index + constructor.Length - 1) : null;
        }

        /// <summary>The brace-matched body of the member whose parameter list opens at or after <paramref name="from"/>.</summary>
        private static string BodyAfter(string code, int from)
        {
            int open = code.IndexOf('{', from);
            int semicolon = code.IndexOf(';', from);
            if (open < 0 || (semicolon >= 0 && semicolon < open)) return null;
            int close = MatchBrace(code, open);
            return close < 0 ? null : code.Substring(open, close - open + 1);
        }

        private static int MatchBrace(string code, int open)
        {
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>Skips the attribute list starting at <paramref name="index"/> and any attribute lists after it.</summary>
        private static int SkipAttributes(string code, int index)
        {
            int i = index;
            while (true)
            {
                while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
                if (i >= code.Length || code[i] != '[') return i;
                int depth = 0;
                for (; i < code.Length; i++)
                {
                    if (code[i] == '[') depth++;
                    else if (code[i] == ']' && --depth == 0) { i++; break; }
                }
            }
        }

        private static List<(string name, int open, int close)> ClassSpans(string code)
        {
            var spans = new List<(string name, int open, int close)>();
            foreach (Match declaration in Regex.Matches(code, @"\b(?:class|struct)\s+(\w+)"))
            {
                int open = code.IndexOf('{', declaration.Index);
                int semicolon = code.IndexOf(';', declaration.Index);
                if (open < 0 || (semicolon >= 0 && semicolon < open)) continue;
                int close = MatchBrace(code, open);
                if (close > open) spans.Add((declaration.Groups[1].Value, open, close));
            }
            return spans;
        }

        private static string EnclosingClass(List<(string name, int open, int close)> spans, int position) =>
            spans.Where(s => s.open < position && position < s.close).OrderByDescending(s => s.open).Select(s => s.name).FirstOrDefault() ?? "?";

        /// <summary>
        /// The source with comments and the contents of string and character literals replaced by spaces (newlines kept),
        /// so the scan only ever matches code. Interpolation holes are parsed, so their nested literals and braces cannot
        /// end a string early.
        /// </summary>
        internal static string MaskNonCode(string source)
        {
            var masked = new StringBuilder(source);
            int i = 0;
            MaskCode(source, masked, ref i, insideHole: false);
            return masked.ToString();
        }

        private static void MaskCode(string s, StringBuilder masked, ref int i, bool insideHole)
        {
            int depth = 0;
            while (i < s.Length)
            {
                char c = s[i];
                char next = i + 1 < s.Length ? s[i + 1] : '\0';
                if (c == '/' && next == '/')
                {
                    while (i < s.Length && s[i] != '\n') masked[i++] = ' ';
                }
                else if (c == '/' && next == '*')
                {
                    int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? s.Length : end + 2;
                    Blank(s, masked, i, end);
                    i = end;
                }
                else if (c == '\'')
                {
                    int start = i++;
                    while (i < s.Length && s[i] != '\'' && s[i] != '\n') i += s[i] == '\\' ? 2 : 1;
                    Blank(s, masked, start + 1, Math.Min(i, s.Length));
                    i++;
                }
                else if (c == '"' || ((c == '$' || c == '@') && (next == '"' || ((next == '$' || next == '@') && i + 2 < s.Length && s[i + 2] == '"'))))
                {
                    MaskString(s, masked, ref i);
                }
                else
                {
                    if (insideHole && c == '{') depth++;
                    else if (insideHole && c == '}' && depth-- == 0) return;
                    i++;
                }
            }
        }

        private static void MaskString(string s, StringBuilder masked, ref int i)
        {
            bool verbatim = false, interpolated = false;
            while (s[i] != '"')
            {
                verbatim |= s[i] == '@';
                interpolated |= s[i] == '$';
                i++;
            }
            int contentStart = ++i;
            while (i < s.Length)
            {
                char c = s[i];
                char next = i + 1 < s.Length ? s[i + 1] : '\0';
                if (verbatim && c == '"' && next == '"') { i += 2; continue; }
                if (!verbatim && c == '\\') { i += 2; continue; }
                if (c == '"') break;
                if (!verbatim && c == '\n') break;
                if (interpolated && (c == '{' || c == '}') && next == c) { i += 2; continue; }
                if (interpolated && c == '{')
                {
                    i++;
                    MaskCode(s, masked, ref i, insideHole: true);
                }
                i++;
            }
            Blank(s, masked, contentStart, Math.Min(i, s.Length));
            i++;
        }

        private static void Blank(string s, StringBuilder masked, int from, int to)
        {
            for (int k = from; k < to; k++)
                if (s[k] != '\n' && s[k] != '\r') masked[k] = ' ';
        }

        private static string EditorSourceRoot()
        {
            var projectRoot = Directory.GetParent(Application.dataPath);
            var inProject = projectRoot == null ? null : Path.Combine(projectRoot.FullName, "SkillsForUnity", "Editor");
            if (inProject != null && Directory.Exists(inProject))
                return inProject;

            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnitySkillAttribute).Assembly);
            Assert.That(packageInfo, Is.Not.Null, "Cannot locate the UnitySkills package sources.");
            var inPackage = Path.Combine(packageInfo.resolvedPath, "Editor");
            Assert.That(Directory.Exists(inPackage), Is.True, $"Missing {inPackage}.");
            return inPackage;
        }
    }
}

// Producer:Betsy
