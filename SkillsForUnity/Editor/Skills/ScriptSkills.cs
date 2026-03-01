using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnitySkills
{
    /// <summary>
    /// Script management skills - create, read, modify.
    /// </summary>
    public static class ScriptSkills
    {
        [UnitySkill("script_create", "Create a new C# script. Optional: namespace")]
        public static object ScriptCreate(string scriptName = null, string name = null, string folder = "Assets/Scripts", string template = null, string namespaceName = null)
        {
            // Support both 'scriptName' and 'name' parameter
            scriptName = scriptName ?? name;
            if (string.IsNullOrEmpty(scriptName))
                return new { error = "scriptName is required" };
            if (scriptName.Contains("/") || scriptName.Contains("\\") || scriptName.Contains(".."))
                return new { error = "scriptName must not contain path separators" };

            if (!string.IsNullOrEmpty(folder) && Validate.SafePath(folder, "folder") is object folderErr) return folderErr;

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, scriptName + ".cs");

            if (File.Exists(path))
                return new { error = $"Script already exists: {path}" };

            // Default template
            string content = template;
            if (string.IsNullOrEmpty(content))
            {
                content = @"using UnityEngine;

namespace {NAMESPACE}
{
    public class {CLASS} : MonoBehaviour
    {
        void Start()
        {
            
        }

        void Update()
        {
            
        }
    }
}
";
                // If no namespace provided, remove namespace wrapper
                if (string.IsNullOrEmpty(namespaceName))
                {
                    content = @"using UnityEngine;

public class {CLASS} : MonoBehaviour
{
    void Start()
    {
        
    }

    void Update()
    {
        
    }
}
";
                }
            }

            content = content.Replace("{CLASS}", scriptName);
            if (!string.IsNullOrEmpty(namespaceName))
                content = content.Replace("{NAMESPACE}", namespaceName);
            else
                content = content.Replace("{NAMESPACE}", "DefaultNamespace");

            File.WriteAllText(path, content);
            AssetDatabase.ImportAsset(path);

            // 记录新创建的脚本（仅元数据，不备份 .cs 内容）
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null) WorkflowManager.SnapshotObject(asset, SnapshotType.Created);

            return new { success = true, path, className = scriptName, namespaceName };
        }

        [UnitySkill("script_create_batch", "Create multiple scripts (Efficient). items: JSON array of {scriptName, folder, template, namespace}")]
        public static object ScriptCreateBatch(string items)
        {
            return BatchExecutor.Execute<BatchScriptItem>(items, item =>
            {
                var result = ScriptCreate(item.scriptName ?? item.name, null, item.folder ?? "Assets/Scripts", item.template, item.namespaceName);
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
                if (json.Contains("\"error\""))
                    throw new System.Exception(((dynamic)result).error);
                return result;
            }, item => item.scriptName ?? item.name);
        }

        private class BatchScriptItem
        {
            public string scriptName { get; set; }
            public string name { get; set; }
            public string folder { get; set; }
            public string template { get; set; }
            public string namespaceName { get; set; }
        }

        [UnitySkill("script_read", "Read the contents of a script")]
        public static object ScriptRead(string scriptPath)
        {
            if (Validate.SafePath(scriptPath, "scriptPath") is object pathErr) return pathErr;
            if (!File.Exists(scriptPath))
                return new { error = $"Script not found: {scriptPath}" };

            var content = File.ReadAllText(scriptPath);
            var lines = content.Split('\n').Length;

            return new { path = scriptPath, lines, content };
        }

        [UnitySkill("script_delete", "Delete a script file")]
        public static object ScriptDelete(string scriptPath)
        {
            if (!File.Exists(scriptPath))
                return new { error = $"Script not found: {scriptPath}" };

            if (Validate.SafePath(scriptPath, "scriptPath", isDelete: true) is object pathErr) return pathErr;

            // 删除前记录脚本元数据
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scriptPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            AssetDatabase.DeleteAsset(scriptPath);
            return new { success = true, deleted = scriptPath };
        }

        [UnitySkill("script_find_in_file", "Search for pattern in scripts")]
        public static object ScriptFindInFile(string pattern, string folder = "Assets", bool isRegex = false, int limit = 50)
        {
            if (!string.IsNullOrEmpty(folder) && Validate.SafePath(folder, "folder") is object folderErr) return folderErr;
            var results = new System.Collections.Generic.List<object>();
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

                    if (match)
                    {
                        results.Add(new
                        {
                            file = file.Replace("\\", "/"),
                            line = i + 1,
                            content = lines[i].Trim()
                        });

                        if (results.Count >= limit) break;
                    }
                }
            }

            return new { pattern, matchCount = results.Count, matches = results };
        }

        [UnitySkill("script_append", "Append content to a script")]
        public static object ScriptAppend(string scriptPath, string content, int atLine = -1)
        {
            if (!File.Exists(scriptPath))
                return new { error = $"Script not found: {scriptPath}" };

            if (Validate.SafePath(scriptPath, "scriptPath") is object pathErr) return pathErr;

            // 修改前记录脚本元数据
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scriptPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);

            var lines = File.ReadAllLines(scriptPath).ToList();

            if (atLine < 0 || atLine >= lines.Count)
            {
                // Append before last closing brace
                var lastBrace = lines.FindLastIndex(l => l.Trim() == "}");
                if (lastBrace > 0)
                    lines.Insert(lastBrace, content);
                else
                    lines.Add(content);
            }
            else
            {
                lines.Insert(atLine, content);
            }

            File.WriteAllLines(scriptPath, lines);
            AssetDatabase.ImportAsset(scriptPath);

            return new { success = true, path = scriptPath };
        }

        [UnitySkill("script_replace", "Find and replace content in a script file")]
        public static object ScriptReplace(string scriptPath, string find, string replace, bool isRegex = false)
        {
            if (!File.Exists(scriptPath))
                return new { error = $"Script not found: {scriptPath}" };
            if (Validate.SafePath(scriptPath, "scriptPath") is object pathErr) return pathErr;
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scriptPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);
            var content = File.ReadAllText(scriptPath);
            string newContent = isRegex
                ? Regex.Replace(content, find, replace, RegexOptions.None, System.TimeSpan.FromSeconds(2))
                : content.Replace(find, replace);
            int changes = isRegex
                ? Regex.Matches(content, find, RegexOptions.None, System.TimeSpan.FromSeconds(2)).Count
                : (content.Length - content.Replace(find, "").Length) / (find.Length > 0 ? find.Length : 1);
            File.WriteAllText(scriptPath, newContent);
            AssetDatabase.ImportAsset(scriptPath);
            return new { success = true, path = scriptPath, replacements = changes };
        }

        [UnitySkill("script_list", "List C# script files in the project")]
        public static object ScriptList(string folder = "Assets", string filter = null, int limit = 100)
        {
            var guids = AssetDatabase.FindAssets("t:MonoScript", new[] { folder });
            var scripts = guids
                .Select(g => AssetDatabase.GUIDToAssetPath(g))
                .Where(p => p.EndsWith(".cs"))
                .Where(p => string.IsNullOrEmpty(filter) || p.Contains(filter))
                .Take(limit)
                .Select(p => new { path = p, name = System.IO.Path.GetFileNameWithoutExtension(p) })
                .ToArray();
            return new { count = scripts.Length, scripts };
        }

        [UnitySkill("script_get_info", "Get script info (class name, base class, methods)")]
        public static object ScriptGetInfo(string scriptPath)
        {
            var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
            if (monoScript == null) return new { error = $"MonoScript not found: {scriptPath}" };
            var type = monoScript.GetClass();
            if (type == null) return new { path = scriptPath, className = "(unknown)", note = "Class not yet compiled or abstract" };
            var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(m => m.Name).ToArray();
            var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(f => new { name = f.Name, type = f.FieldType.Name }).ToArray();
            return new
            {
                path = scriptPath, className = type.Name,
                baseClass = type.BaseType?.Name,
                namespaceName = type.Namespace,
                isMonoBehaviour = typeof(MonoBehaviour).IsAssignableFrom(type),
                publicMethods = methods, publicFields = fields
            };
        }

        [UnitySkill("script_rename", "Rename a script file")]
        public static object ScriptRename(string scriptPath, string newName)
        {
            if (!File.Exists(scriptPath)) return new { error = $"Script not found: {scriptPath}" };
            if (Validate.SafePath(scriptPath, "scriptPath") is object pathErr) return pathErr;
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scriptPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);
            var result = AssetDatabase.RenameAsset(scriptPath, newName);
            if (!string.IsNullOrEmpty(result)) return new { error = result };
            return new { success = true, oldPath = scriptPath, newName };
        }

        [UnitySkill("script_move", "Move a script to a new folder")]
        public static object ScriptMove(string scriptPath, string newFolder)
        {
            if (!File.Exists(scriptPath)) return new { error = $"Script not found: {scriptPath}" };
            if (Validate.SafePath(scriptPath, "scriptPath") is object pathErr) return pathErr;
            if (!Directory.Exists(newFolder)) Directory.CreateDirectory(newFolder);
            var fileName = System.IO.Path.GetFileName(scriptPath);
            var newPath = System.IO.Path.Combine(newFolder, fileName);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scriptPath);
            if (asset != null) WorkflowManager.SnapshotObject(asset);
            var result = AssetDatabase.MoveAsset(scriptPath, newPath);
            if (!string.IsNullOrEmpty(result)) return new { error = result };
            return new { success = true, oldPath = scriptPath, newPath };
        }
    }
}
