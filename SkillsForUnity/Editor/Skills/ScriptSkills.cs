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
            if (string.IsNullOrEmpty(items))
                return new { error = "items parameter is required." };

            try
            {
                var itemList = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<BatchScriptItem>>(items);
                if (itemList == null || itemList.Count == 0)
                    return new { error = "items parameter is empty or invalid JSON" };

                var results = new System.Collections.Generic.List<object>();
                int successCount = 0;
                int failCount = 0;

                foreach (var item in itemList)
                {
                    try
                    {
                        var result = ScriptCreate(item.scriptName, item.folder ?? "Assets/Scripts", item.template, item.namespaceName);
                        
                        // Check if result is error anonymously
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(result);
                        if (json.Contains("\"error\""))
                        {
                            results.Add(new { target = item.scriptName, success = false, error = json });
                            failCount++;
                        }
                        else
                        {
                            results.Add(result);
                            successCount++;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        results.Add(new { target = item.scriptName, success = false, error = ex.Message });
                        failCount++;
                    }
                }

                return new
                {
                    success = failCount == 0,
                    totalItems = itemList.Count,
                    successCount,
                    failCount,
                    results,
                    message = "Scripts created. Expect Domain Reload."
                };
            }
            catch (System.Exception ex)
            {
                return new { error = $"Failed to parse items JSON: {ex.Message}" };
            }
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
                        ? Regex.IsMatch(lines[i], pattern)
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
