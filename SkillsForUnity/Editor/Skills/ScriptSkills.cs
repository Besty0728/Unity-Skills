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
        public static object ScriptCreate(string scriptName, string folder = "Assets/Scripts", string template = null, string namespaceName = null)
        {
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
                var result = ScriptCreate(item.scriptName, item.folder ?? "Assets/Scripts", item.template, item.namespaceName);
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
                if (json.Contains("\"error\""))
                    throw new System.Exception(((dynamic)result).error);
                return result;
            }, item => item.scriptName);
        }

        private class BatchScriptItem
        {
            public string scriptName { get; set; }
            public string folder { get; set; }
            public string template { get; set; }
            public string namespaceName { get; set; }
        }

        [UnitySkill("script_read", "Read the contents of a script")]
        public static object ScriptRead(string scriptPath)
        {
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
            var results = new System.Collections.Generic.List<object>();
            var files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                if (results.Count >= limit) break;

                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    bool match = isRegex
                        ? Regex.IsMatch(lines[i], pattern, RegexOptions.None, TimeSpan.FromSeconds(1))
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
    }
}
