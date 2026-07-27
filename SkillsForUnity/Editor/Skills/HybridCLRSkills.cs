using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace UnitySkills
{
    /// <summary>
    /// HybridCLR (com.code-philosophy.hybridclr) Editor skills — C# hot-update prebuild
    /// orchestration: settings read/write, il2cpp_plus install probing, hot-update assembly
    /// compilation, and generation-pipeline execution.
    ///
    /// The package is optional and this module keeps ZERO direct references to it: every call
    /// resolves through reflection against the `HybridCLR.Editor` assembly, so the UnitySkills
    /// Editor assembly compiles identically with or without HybridCLR present. `hybridclr_status`
    /// works either way; every other skill returns <see cref="NoHybridCLR"/> when the package
    /// is missing.
    ///
    /// API anchors follow hybridclr_unity 8.12.0 Editor source (HybridCLR.Editor.SettingsUtil,
    /// HybridCLR.Editor.Settings.HybridCLRSettings, HybridCLR.Editor.Commands.*,
    /// HybridCLR.Editor.Installer.InstallerController).
    /// </summary>
    public static class HybridCLRSkills
    {
        private const string EditorAssemblyName = "HybridCLR.Editor";
        private const string PackageId = "com.code-philosophy.hybridclr";
        private const string DocsUrl = "https://hybridclr.doc.code-philosophy.com/docs/beginner/quickstart";

        private const string SettingsKey = "hybridclr.settings";
        private const string GeneratedSourcesKey = "hybridclr.generatedSources";
        private const string FileSetKey = "hybridclr.fileSet";

        // Backups written by hybridclr_compile_dlls / hybridclr_copy_hotupdate_dlls live outside the
        // workflow blob store, so nothing prunes them for us. Keep a bounded ring per label.
        private const int MaxBackupGenerations = 5;

        // ==================================================================================
        // Reflection layer — resolves HybridCLR.Editor lazily, never links against it.
        // ==================================================================================

        private static Assembly _editorAssembly;

        private static Assembly EditorAssembly()
        {
            if (_editorAssembly != null)
                return _editorAssembly;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = asm.GetName().Name; }
                catch { continue; }

                if (string.Equals(name, EditorAssemblyName, StringComparison.Ordinal))
                {
                    _editorAssembly = asm;
                    break;
                }
            }
            return _editorAssembly;
        }

        private static bool Installed => EditorAssembly() != null;

        private static Type HclrType(string fullName)
        {
            var asm = EditorAssembly();
            if (asm == null) return null;
            try { return asm.GetType(fullName, false); }
            catch { return null; }
        }

        private static Type SettingsUtilType => HclrType("HybridCLR.Editor.SettingsUtil");
        private static Type SettingsType => HclrType("HybridCLR.Editor.Settings.HybridCLRSettings");
        private static Type InstallerControllerType => HclrType("HybridCLR.Editor.Installer.InstallerController");

        private static object NoHybridCLR() => new
        {
            error = $"HybridCLR package ({PackageId}) is not installed — the 'HybridCLR.Editor' assembly could not be resolved. " +
                    "Install it via Window > Package Manager > Add package from git URL > " +
                    "https://github.com/focus-creative-games/hybridclr_unity.git, then run the HybridCLR/Installer window once.",
            errorCode = "MISSING_PACKAGE",
            requiredPackage = PackageId,
            docs = DocsUrl,
            hint = "Call hybridclr_status first — it is the only skill in this module that works without the package."
        };

        private static object MissingApi(string member) => new
        {
            error = $"HybridCLR.Editor is present but '{member}' could not be resolved by reflection. " +
                    "The installed HybridCLR version likely differs from the 8.12.0 API this module targets.",
            errorCode = "MISSING_PACKAGE",
            member,
            hint = "Fall back to the equivalent HybridCLR menu command via unity-cli -executeMethod, or upgrade/downgrade the package.",
            docs = DocsUrl
        };

        /// <summary>Reads a public static property off a HybridCLR type; returns null on any failure.</summary>
        private static object StaticProp(Type type, string name)
        {
            try
            {
                var p = type?.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                return p?.GetValue(null);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[HybridCLR] static property '{name}' read failed: {ex.Message}");
                return null;
            }
        }

        private static string StaticPropString(Type type, string name) => StaticProp(type, name) as string;

        private static List<string> StaticPropStringList(Type type, string name, out string error)
        {
            error = null;
            try
            {
                var p = type?.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                if (p == null) return null;
                return (p.GetValue(null) as IEnumerable<string>)?.ToList();
            }
            catch (Exception ex)
            {
                // SettingsUtil.HotUpdateAssemblyNamesIncludePreserved throws on duplicate preserved
                // names, which is a user configuration error worth surfacing rather than swallowing.
                error = (ex.InnerException ?? ex).Message;
                return null;
            }
        }

        private static object SettingsInstance()
        {
            var t = SettingsType;
            if (t == null) return null;
            try { return t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null); }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[HybridCLR] HybridCLRSettings.Instance failed: {ex.Message}");
                return null;
            }
        }

        private static bool SaveSettings()
        {
            try
            {
                var m = SettingsType?.GetMethod("Save", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (m == null) return false;
                m.Invoke(null, null);
                return true;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[HybridCLR] HybridCLRSettings.Save failed: {ex.Message}");
                return false;
            }
        }

        private static FieldInfo SettingsField(string name) =>
            SettingsType?.GetField(name, BindingFlags.Public | BindingFlags.Instance);

        private static string HotUpdateDllsDirFor(BuildTarget target)
        {
            try
            {
                var m = SettingsUtilType?.GetMethod("GetHotUpdateDllsOutputDirByTarget",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(BuildTarget) }, null);
                return m?.Invoke(null, new object[] { target }) as string;
            }
            catch { return null; }
        }

        private static string StrippedAotDirFor(BuildTarget target)
        {
            try
            {
                var m = SettingsUtilType?.GetMethod("GetAssembliesPostIl2CppStripDir",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(BuildTarget) }, null);
                return m?.Invoke(null, new object[] { target }) as string;
            }
            catch { return null; }
        }

        // ==================================================================================
        // Path helpers
        // ==================================================================================

        private static string ProjectRoot()
        {
            var fromSettings = StaticPropString(SettingsUtilType, "ProjectDir");
            if (!string.IsNullOrEmpty(fromSettings))
                return Normalize(fromSettings);

            return Normalize(Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath);
        }

        private static string Normalize(string path) =>
            string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/').TrimEnd('/');

        /// <summary>
        /// HybridCLR stores output roots as project-relative strings ("HybridCLRData/HotUpdateDlls"),
        /// but also accepts absolute ones. Resolve against the project root either way.
        /// </summary>
        private static string ResolveProjectPath(string maybeRelative)
        {
            if (string.IsNullOrEmpty(maybeRelative)) return null;
            var p = maybeRelative.Replace('\\', '/');
            try
            {
                return Normalize(Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(ProjectRoot(), p)));
            }
            catch { return p; }
        }

        private static bool IsInsideProject(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return false;
            var root = ProjectRoot();
            if (string.IsNullOrEmpty(root)) return false;
            var p = Normalize(absolutePath);
            return p.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string ToProjectRelative(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return null;
            var root = ProjectRoot();
            var p = Normalize(absolutePath);
            if (!string.IsNullOrEmpty(root) && p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return p.Substring(root.Length + 1);
            return p;
        }

        private static string BackupRoot() => $"{ProjectRoot()}/Library/UnitySkills/HybridCLRBackups";

        private static bool TryParseBuildTarget(string value, out BuildTarget target, out object error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                target = EditorUserBuildSettings.activeBuildTarget;
                return true;
            }

            if (Enum.TryParse(value, true, out target) && Enum.IsDefined(typeof(BuildTarget), target))
                return true;

            error = new
            {
                error = $"Unknown buildTarget '{value}'.",
                hint = "Omit buildTarget to use EditorUserBuildSettings.activeBuildTarget.",
                available = new[] { "StandaloneWindows64", "StandaloneOSX", "StandaloneLinux64", "Android", "iOS", "WebGL" }
            };
            target = EditorUserBuildSettings.activeBuildTarget;
            return false;
        }

        private static object FileInfoPayload(string absolutePath)
        {
            bool exists = !string.IsNullOrEmpty(absolutePath) && File.Exists(absolutePath);
            long size = 0;
            string modified = null;
            if (exists)
            {
                try
                {
                    var fi = new FileInfo(absolutePath);
                    size = fi.Length;
                    modified = fi.LastWriteTimeUtc.ToString("o");
                }
                catch { /* best-effort metadata */ }
            }
            return new
            {
                path = Normalize(absolutePath),
                projectRelativePath = ToProjectRelative(absolutePath),
                exists,
                sizeBytes = size,
                lastWriteUtc = modified
            };
        }

        // ==================================================================================
        // Workflow snapshot restorers (registered on domain load)
        // ==================================================================================

        private sealed class SettingsSnapshot
        {
            public bool enable;
            public bool useGlobalIl2cpp;
            public string hybridclrRepoURL;
            public string il2cppPlusRepoURL;
            public string[] hotUpdateAssemblyDefinitions;
            public string[] hotUpdateAssemblies;
            public string[] preserveHotUpdateAssemblies;
            public string hotUpdateDllCompileOutputRootDir;
            public string[] externalHotUpdateAssembliyDirs;
            public string strippedAOTDllOutputRootDir;
            public string[] patchAOTAssemblies;
            public string outputLinkFile;
            public string outputAOTGenericReferenceFile;
            public int maxGenericReferenceIteration;
            public int maxMethodBridgeGenericIteration;
        }

        private sealed class GeneratedSourceEntry
        {
            public string path;
            public bool existed;
            public string text;
        }

        private sealed class GeneratedSourceSnapshot
        {
            public GeneratedSourceEntry[] files;
        }

        private sealed class FileSetBackup
        {
            public string label;
            public string targetDir;
            public bool targetDirExisted;
            public string backupDir;
            public string[] files;
            public bool refreshAssetDatabase;
        }

        [InitializeOnLoadMethod]
        private static void RegisterSettingRestorers()
        {
            WorkflowSettingRestorerRegistry.Register(SettingsKey, CaptureSettingsJson, ApplySettingsJson);
            // Generated sources and DLL file sets have no meaningful "read current value" form —
            // capturing a redo value would mean producing another backup — so both use the
            // restorer-only overload.
            WorkflowSettingRestorerRegistry.Register(GeneratedSourcesKey, ApplyGeneratedSources);
            WorkflowSettingRestorerRegistry.Register(FileSetKey, ApplyFileSetBackup);
        }

        private static SettingsSnapshot CaptureSettings()
        {
            var s = SettingsInstance();
            if (s == null) return null;

            string[] Arr(string field)
            {
                var f = SettingsField(field);
                return (f?.GetValue(s) as string[]) ?? Array.Empty<string>();
            }

            T Val<T>(string field, T fallback)
            {
                var f = SettingsField(field);
                var v = f?.GetValue(s);
                return v is T typed ? typed : fallback;
            }

            var asmdefField = SettingsField("hotUpdateAssemblyDefinitions");
            var asmdefPaths = (asmdefField?.GetValue(s) as UnityEngine.Object[])
                ?.Where(o => o != null)
                .Select(o => AssetDatabase.GetAssetPath(o))
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray() ?? Array.Empty<string>();

            return new SettingsSnapshot
            {
                enable = Val("enable", true),
                useGlobalIl2cpp = Val("useGlobalIl2cpp", false),
                hybridclrRepoURL = Val<string>("hybridclrRepoURL", null),
                il2cppPlusRepoURL = Val<string>("il2cppPlusRepoURL", null),
                hotUpdateAssemblyDefinitions = asmdefPaths,
                hotUpdateAssemblies = Arr("hotUpdateAssemblies"),
                preserveHotUpdateAssemblies = Arr("preserveHotUpdateAssemblies"),
                hotUpdateDllCompileOutputRootDir = Val<string>("hotUpdateDllCompileOutputRootDir", null),
                externalHotUpdateAssembliyDirs = Arr("externalHotUpdateAssembliyDirs"),
                strippedAOTDllOutputRootDir = Val<string>("strippedAOTDllOutputRootDir", null),
                patchAOTAssemblies = Arr("patchAOTAssemblies"),
                outputLinkFile = Val<string>("outputLinkFile", null),
                outputAOTGenericReferenceFile = Val<string>("outputAOTGenericReferenceFile", null),
                maxGenericReferenceIteration = Val("maxGenericReferenceIteration", 10),
                maxMethodBridgeGenericIteration = Val("maxMethodBridgeGenericIteration", 10)
            };
        }

        private static string CaptureSettingsJson()
        {
            var snap = CaptureSettings();
            return snap == null ? null : JsonConvert.SerializeObject(snap);
        }

        private static bool ApplySettingsJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;

            var s = SettingsInstance();
            if (s == null) return false;

            SettingsSnapshot snap;
            try { snap = JsonConvert.DeserializeObject<SettingsSnapshot>(json); }
            catch { return false; }
            if (snap == null) return false;

            SettingsField("enable")?.SetValue(s, snap.enable);
            SettingsField("useGlobalIl2cpp")?.SetValue(s, snap.useGlobalIl2cpp);
            SettingsField("hybridclrRepoURL")?.SetValue(s, snap.hybridclrRepoURL);
            SettingsField("il2cppPlusRepoURL")?.SetValue(s, snap.il2cppPlusRepoURL);
            SettingsField("hotUpdateAssemblies")?.SetValue(s, snap.hotUpdateAssemblies ?? Array.Empty<string>());
            SettingsField("preserveHotUpdateAssemblies")?.SetValue(s, snap.preserveHotUpdateAssemblies ?? Array.Empty<string>());
            SettingsField("hotUpdateDllCompileOutputRootDir")?.SetValue(s, snap.hotUpdateDllCompileOutputRootDir);
            SettingsField("externalHotUpdateAssembliyDirs")?.SetValue(s, snap.externalHotUpdateAssembliyDirs ?? Array.Empty<string>());
            SettingsField("strippedAOTDllOutputRootDir")?.SetValue(s, snap.strippedAOTDllOutputRootDir);
            SettingsField("patchAOTAssemblies")?.SetValue(s, snap.patchAOTAssemblies ?? Array.Empty<string>());
            SettingsField("outputLinkFile")?.SetValue(s, snap.outputLinkFile);
            SettingsField("outputAOTGenericReferenceFile")?.SetValue(s, snap.outputAOTGenericReferenceFile);
            SettingsField("maxGenericReferenceIteration")?.SetValue(s, snap.maxGenericReferenceIteration);
            SettingsField("maxMethodBridgeGenericIteration")?.SetValue(s, snap.maxMethodBridgeGenericIteration);

            if (!AssignAsmdefs(s, snap.hotUpdateAssemblyDefinitions, out var missing) && missing.Count > 0)
                SkillsLogger.LogWarning($"[HybridCLR] settings restore could not resolve asmdef assets: {string.Join(", ", missing)}");

            return SaveSettings();
        }

        private static bool AssignAsmdefs(object settings, string[] assetPaths, out List<string> unresolved)
        {
            unresolved = new List<string>();
            var field = SettingsField("hotUpdateAssemblyDefinitions");
            if (field == null) return false;

            var resolved = new List<AssemblyDefinitionAsset>();
            foreach (var p in assetPaths ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var asset = AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(p);
                if (asset == null) unresolved.Add(p);
                else resolved.Add(asset);
            }

            field.SetValue(settings, resolved.ToArray());
            return unresolved.Count == 0;
        }

        private static GeneratedSourceSnapshot CaptureGeneratedSources()
        {
            var entries = new List<GeneratedSourceEntry>();
            foreach (var abs in GeneratedSourceFiles())
            {
                bool exists = File.Exists(abs);
                string text = null;
                if (exists)
                {
                    try { text = File.ReadAllText(abs); }
                    catch (Exception ex)
                    {
                        SkillsLogger.LogWarning($"[HybridCLR] could not read '{abs}' for snapshot: {ex.Message}");
                        exists = false;
                    }
                }
                entries.Add(new GeneratedSourceEntry { path = Normalize(abs), existed = exists, text = text });
            }
            return new GeneratedSourceSnapshot { files = entries.ToArray() };
        }

        /// <summary>
        /// The two Assets-side artifacts HybridCLR regenerates: link.xml and AOTGenericReferences.cs.
        /// Everything else the pipeline emits lands in HybridCLRData/ or LocalIl2CppData-*/ and is a
        /// rebuildable intermediate, so it is deliberately outside the snapshot.
        /// </summary>
        private static List<string> GeneratedSourceFiles()
        {
            var result = new List<string>();
            foreach (var p in new[] { LinkXmlPath(), AotGenericReferencesPath() })
            {
                if (!string.IsNullOrEmpty(p)) result.Add(p);
            }
            return result;
        }

        /// <summary>Resolves a settings field holding an Assets-relative path. Null when unset.</summary>
        private static string AssetsRelativeSetting(string fieldName)
        {
            var s = SettingsInstance();
            if (s == null) return null;
            var rel = SettingsField(fieldName)?.GetValue(s) as string;
            return string.IsNullOrWhiteSpace(rel) ? null : Normalize($"{Application.dataPath}/{rel}");
        }

        private static string LinkXmlPath() => AssetsRelativeSetting("outputLinkFile");

        private static string AotGenericReferencesPath() => AssetsRelativeSetting("outputAOTGenericReferenceFile");

        private static bool ApplyGeneratedSources(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;

            GeneratedSourceSnapshot snap;
            try { snap = JsonConvert.DeserializeObject<GeneratedSourceSnapshot>(json); }
            catch { return false; }
            if (snap?.files == null) return false;

            foreach (var entry in snap.files)
            {
                if (entry == null || string.IsNullOrEmpty(entry.path)) continue;
                if (!IsInsideProject(entry.path)) continue;

                try
                {
                    if (entry.existed)
                    {
                        var dir = Path.GetDirectoryName(entry.path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(entry.path, entry.text ?? string.Empty);
                    }
                    else if (File.Exists(entry.path))
                    {
                        File.Delete(entry.path);
                        var meta = entry.path + ".meta";
                        if (File.Exists(meta)) File.Delete(meta);
                    }
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogWarning($"[HybridCLR] generated-source restore failed for '{entry.path}': {ex.Message}");
                }
            }

            AssetDatabase.Refresh();
            return true;
        }

        private static FileSetBackup CaptureFileSet(string label, string targetDir, bool refreshAssetDatabase)
        {
            var snap = new FileSetBackup
            {
                label = label,
                targetDir = Normalize(targetDir),
                targetDirExisted = !string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir),
                refreshAssetDatabase = refreshAssetDatabase,
                files = Array.Empty<string>(),
                backupDir = null
            };

            if (!snap.targetDirExisted) return snap;

            string[] files;
            try { files = Directory.GetFiles(targetDir, "*", SearchOption.TopDirectoryOnly); }
            catch { return snap; }
            if (files.Length == 0) return snap;

            var backupDir = $"{BackupRoot()}/{label}/{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
            var names = new List<string>(files.Length);
            try
            {
                Directory.CreateDirectory(backupDir);
                foreach (var f in files)
                {
                    var n = Path.GetFileName(f);
                    try
                    {
                        File.Copy(f, Path.Combine(backupDir, n), true);
                        names.Add(n);
                    }
                    catch (Exception ex)
                    {
                        SkillsLogger.LogWarning($"[HybridCLR] backup skipped '{n}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[HybridCLR] backup directory '{backupDir}' unavailable: {ex.Message}");
                return snap;
            }

            snap.backupDir = Normalize(backupDir);
            snap.files = names.ToArray();
            PruneBackups(label);
            return snap;
        }

        private static void PruneBackups(string label)
        {
            try
            {
                var root = $"{BackupRoot()}/{label}";
                if (!Directory.Exists(root)) return;

                var generations = Directory.GetDirectories(root)
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                    .Skip(MaxBackupGenerations)
                    .ToList();

                foreach (var d in generations)
                {
                    try { Directory.Delete(d, true); }
                    catch { /* a locked backup simply survives one extra round */ }
                }
            }
            catch { /* pruning is never load-bearing */ }
        }

        private static bool ApplyFileSetBackup(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;

            FileSetBackup snap;
            try { snap = JsonConvert.DeserializeObject<FileSetBackup>(json); }
            catch { return false; }
            if (snap == null || string.IsNullOrEmpty(snap.targetDir)) return false;
            if (!IsInsideProject(snap.targetDir)) return false;

            var tracked = new HashSet<string>(snap.files ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            // Remove whatever the operation added on top of the tracked set.
            if (Directory.Exists(snap.targetDir))
            {
                foreach (var f in Directory.GetFiles(snap.targetDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var n = Path.GetFileName(f);
                    if (tracked.Contains(n)) continue;
                    try
                    {
                        File.Delete(f);
                        var meta = f + ".meta";
                        if (File.Exists(meta)) File.Delete(meta);
                    }
                    catch (Exception ex)
                    {
                        SkillsLogger.LogWarning($"[HybridCLR] could not remove '{n}' during restore: {ex.Message}");
                    }
                }
            }

            // Put the pre-operation files back.
            if (tracked.Count > 0 && !string.IsNullOrEmpty(snap.backupDir) && Directory.Exists(snap.backupDir))
            {
                try { Directory.CreateDirectory(snap.targetDir); } catch { }
                foreach (var n in tracked)
                {
                    var src = Path.Combine(snap.backupDir, n);
                    if (!File.Exists(src)) continue;
                    try { File.Copy(src, Path.Combine(snap.targetDir, n), true); }
                    catch (Exception ex)
                    {
                        SkillsLogger.LogWarning($"[HybridCLR] could not restore '{n}': {ex.Message}");
                    }
                }
            }

            if (!snap.targetDirExisted && Directory.Exists(snap.targetDir))
            {
                try
                {
                    if (Directory.GetFileSystemEntries(snap.targetDir).Length == 0)
                    {
                        Directory.Delete(snap.targetDir);
                        var meta = snap.targetDir + ".meta";
                        if (File.Exists(meta)) File.Delete(meta);
                    }
                }
                catch { /* leaving an empty directory behind is harmless */ }
            }

            if (snap.refreshAssetDatabase) AssetDatabase.Refresh();
            return true;
        }

        // ==================================================================================
        // A. Environment (3 skills) — hybridclr_status works WITHOUT the package
        // ==================================================================================

        [UnitySkill("hybridclr_status",
            "Report HybridCLR installation status, package version, whether HybridCLR is enabled, the configured hot-update / AOT-patch assembly lists, and which generated artifacts already exist. Runs with or without the package installed — call this first.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Query,
            Tags = new[] { "hybridclr", "hotupdate", "status", "environment", "check" },
            Outputs = new[] { "installed", "packageVersion", "enable", "hotUpdateAssemblies", "patchAOTAssemblies", "il2cppInstalled", "generatedArtifacts" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object Status()
        {
            if (!Installed)
            {
                return new
                {
                    installed = false,
                    reason = "Assembly 'HybridCLR.Editor' is not loaded in the current AppDomain.",
                    requiredPackage = PackageId,
                    hint = "Install via Package Manager git URL: https://github.com/focus-creative-games/hybridclr_unity.git, then open HybridCLR/Installer... and click Install.",
                    docs = DocsUrl
                };
            }

            var settings = SettingsInstance();
            var target = EditorUserBuildSettings.activeBuildTarget;

            var hotUpdateNames = StaticPropStringList(SettingsUtilType, "HotUpdateAssemblyNamesExcludePreserved", out var hotUpdateError);
            var aotNames = StaticPropStringList(SettingsUtilType, "AOTAssemblyNames", out _);

            string packageVersion = null;
            try
            {
                var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssembly(EditorAssembly());
                packageVersion = pkg?.version;
            }
            catch { /* best-effort */ }

            var installer = new InstallerHandle();
            bool il2cppInstalled = installer.HasInstalledHybridCLR() ?? false;
            string installedLibil2cppVersion = installer.StringMember("InstalledLibil2cppVersion");
            packageVersion ??= installer.StringMember("PackageVersion");

            var linkXml = LinkXmlPath();
            var aotRefs = AotGenericReferencesPath();
            var dllDir = ResolveProjectPath(HotUpdateDllsDirFor(target));

            return new
            {
                installed = true,
                packageId = PackageId,
                packageVersion,
                editorAssembly = EditorAssembly()?.GetName().Version?.ToString(),
                enable = settings == null ? (bool?)null : StaticProp(SettingsUtilType, "Enable") as bool?,
                scriptingBackend = ActiveScriptingBackend(),
                activeBuildTarget = target.ToString(),
                il2cppInstalled,
                installedLibil2cppVersion,
                hotUpdateAssemblies = hotUpdateNames?.ToArray() ?? Array.Empty<string>(),
                hotUpdateAssemblyResolveError = hotUpdateError,
                patchAOTAssemblies = aotNames?.ToArray() ?? Array.Empty<string>(),
                generatedArtifacts = new
                {
                    linkXml = FileInfoPayload(linkXml),
                    aotGenericReferences = FileInfoPayload(aotRefs),
                    hotUpdateDllDir = new
                    {
                        path = dllDir,
                        projectRelativePath = ToProjectRelative(dllDir),
                        exists = !string.IsNullOrEmpty(dllDir) && Directory.Exists(dllDir)
                    }
                },
                docs = DocsUrl
            };
        }

        [UnitySkill("hybridclr_install_status",
            "Report the local il2cpp_plus installation state via HybridCLR.Editor.Installer.InstallerController — whether libil2cpp has been patched, the installed libil2cpp version vs the package version, the expected hybridclr / il2cpp_plus branches, and Unity-version compatibility.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Query,
            Tags = new[] { "hybridclr", "installer", "il2cpp", "libil2cpp", "status" },
            Outputs = new[] { "hasInstalled", "packageVersion", "installedLibil2cppVersion", "versionMatched", "compatibility", "localIl2CppDir" },
            ReadOnly = true,
            RequiresPackages = new[] { PackageId },
            Mode = SkillMode.SemiAuto)]
        public static object InstallStatus()
        {
            if (!Installed) return NoHybridCLR();
            if (InstallerControllerType == null) return MissingApi("HybridCLR.Editor.Installer.InstallerController");

            var installer = new InstallerHandle();
            if (installer.Error != null)
            {
                return new
                {
                    error = $"InstallerController could not be constructed: {installer.Error}",
                    hint = "This usually means the package's Data~/hybridclr_version.json is missing or the Unity version is unrecognised. Open HybridCLR/Installer... to inspect.",
                    docs = DocsUrl
                };
            }

            var packageVersion = installer.StringMember("PackageVersion");
            var installedVersion = installer.StringMember("InstalledLibil2cppVersion");
            bool hasInstalled = installer.HasInstalledHybridCLR() ?? false;
            var localIl2CppDir = StaticPropString(SettingsUtilType, "LocalIl2CppDir");

            return new
            {
                hasInstalled,
                packageVersion,
                installedLibil2cppVersion = installedVersion,
                versionMatched = hasInstalled && !string.IsNullOrEmpty(packageVersion) &&
                                 string.Equals(packageVersion, installedVersion?.Trim(), StringComparison.Ordinal),
                unityMajorVersion = installer.Member("MajorVersion"),
                unityVersion = Application.unityVersion,
                compatibility = installer.Invoke("GetCompatibleType")?.ToString(),
                minCompatibleUnityVersion = installer.Invoke("GetCurrentUnityVersionMinCompatibleVersionStr") as string,
                expectedHybridclrBranch = installer.StringMember("HybridclrLocalVersion"),
                expectedIl2cppPlusBranch = installer.StringMember("Il2cppPlusLocalVersion"),
                localIl2CppDir = Normalize(localIl2CppDir),
                localVersionFile = Normalize(installer.StringMember("LocalVersionFile")),
                note = hasInstalled
                    ? "Installation present. A versionMatched=false result means the package was upgraded after installing — re-run HybridCLR/Installer."
                    : "libil2cpp has not been patched yet. Installation clones two git repos and copies the editor's il2cpp tree; it is intentionally NOT exposed as a skill (long-running network + filesystem operation). Run it from HybridCLR/Installer... in the Editor.",
                docs = "https://hybridclr.doc.code-philosophy.com/docs/beginner/install"
            };
        }

        [UnitySkill("hybridclr_get_paths",
            "Resolve every HybridCLR input/output path for a build target — HybridCLRData root, local il2cpp dir, hot-update DLL output dir, stripped AOT assemblies dir, link.xml and AOTGenericReferences.cs. Use this to wire HybridCLR outputs into a YooAsset collector directory.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Query,
            Tags = new[] { "hybridclr", "paths", "output", "directory", "yooasset" },
            Outputs = new[] { "projectDir", "hotUpdateDllsDir", "strippedAotDir", "linkXml", "aotGenericReferences" },
            ReadOnly = true,
            RequiresPackages = new[] { PackageId },
            Mode = SkillMode.SemiAuto)]
        public static object GetPaths(string buildTarget = null)
        {
            if (!Installed) return NoHybridCLR();
            if (SettingsUtilType == null) return MissingApi("HybridCLR.Editor.SettingsUtil");
            if (!TryParseBuildTarget(buildTarget, out var target, out var targetError)) return targetError;

            var dllDir = ResolveProjectPath(HotUpdateDllsDirFor(target));
            var aotDir = ResolveProjectPath(StrippedAotDirFor(target));

            return new
            {
                buildTarget = target.ToString(),
                projectDir = ProjectRoot(),
                hybridCLRDataDir = Normalize(StaticPropString(SettingsUtilType, "HybridCLRDataDir")),
                localUnityDataDir = Normalize(StaticPropString(SettingsUtilType, "LocalUnityDataDir")),
                localIl2CppDir = Normalize(StaticPropString(SettingsUtilType, "LocalIl2CppDir")),
                generatedCppDir = Normalize(StaticPropString(SettingsUtilType, "GeneratedCppDir")),
                hotUpdateDllsRootOutputDir = ResolveProjectPath(StaticPropString(SettingsUtilType, "HotUpdateDllsRootOutputDir")),
                hotUpdateDllsDir = new
                {
                    path = dllDir,
                    projectRelativePath = ToProjectRelative(dllDir),
                    exists = !string.IsNullOrEmpty(dllDir) && Directory.Exists(dllDir)
                },
                strippedAotDir = new
                {
                    path = aotDir,
                    projectRelativePath = ToProjectRelative(aotDir),
                    exists = !string.IsNullOrEmpty(aotDir) && Directory.Exists(aotDir)
                },
                linkXml = FileInfoPayload(LinkXmlPath()),
                aotGenericReferences = FileInfoPayload(AotGenericReferencesPath()),
                note = "hotUpdateDllsRootOutputDir and strippedAOTDllOutputRootDir are stored project-relative in HybridCLRSettings; the absolute forms above are resolved against projectDir."
            };
        }

        // ==================================================================================
        // B. Settings (2 skills)
        // ==================================================================================

        [UnitySkill("hybridclr_settings_get",
            "Read HybridCLRSettings (ProjectSettings/HybridCLRSettings.asset) — every configuration field plus the resolved hot-update assembly name/file lists that SettingsUtil derives from asmdef assets and raw names.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Query,
            Tags = new[] { "hybridclr", "settings", "config", "assemblies", "read" },
            Outputs = new[] { "settings", "resolved", "settingsAssetPath" },
            ReadOnly = true,
            RequiresPackages = new[] { PackageId },
            Mode = SkillMode.SemiAuto)]
        public static object SettingsGet()
        {
            if (!Installed) return NoHybridCLR();

            var snap = CaptureSettings();
            if (snap == null) return MissingApi("HybridCLR.Editor.Settings.HybridCLRSettings.Instance");

            var namesExcl = StaticPropStringList(SettingsUtilType, "HotUpdateAssemblyNamesExcludePreserved", out var exclError);
            var namesIncl = StaticPropStringList(SettingsUtilType, "HotUpdateAssemblyNamesIncludePreserved", out var inclError);
            var filesIncl = StaticPropStringList(SettingsUtilType, "HotUpdateAssemblyFilesIncludePreserved", out _);

            var missingAsmdefs = (snap.hotUpdateAssemblyDefinitions ?? Array.Empty<string>())
                .Where(p => AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(p) == null)
                .ToArray();

            return new
            {
                settingsAssetPath = "ProjectSettings/HybridCLRSettings.asset",
                settings = snap,
                resolved = new
                {
                    hotUpdateAssemblyNamesExcludePreserved = namesExcl?.ToArray() ?? Array.Empty<string>(),
                    hotUpdateAssemblyNamesIncludePreserved = namesIncl?.ToArray() ?? Array.Empty<string>(),
                    hotUpdateAssemblyFilesIncludePreserved = filesIncl?.ToArray() ?? Array.Empty<string>(),
                    patchAOTAssemblies = snap.patchAOTAssemblies,
                    resolveErrors = new[] { exclError, inclError }.Where(e => !string.IsNullOrEmpty(e)).ToArray(),
                    unresolvedAssemblyDefinitions = missingAsmdefs
                },
                notes = new[]
                {
                    "HybridCLRSettings is a ScriptableObject serialized to ProjectSettings/, not an AssetDatabase asset — it will not appear in asset_* skills.",
                    "outputLinkFile and outputAOTGenericReferenceFile are relative to Assets/; the two output root dirs are relative to the project directory.",
                    "The field name 'externalHotUpdateAssembliyDirs' is spelled that way upstream (typo in HybridCLR source); the skill parameter is spelled 'externalHotUpdateAssemblyDirs'."
                }
            };
        }

        [UnitySkill("hybridclr_settings_set",
            "Write HybridCLRSettings fields (enable, hot-update assembly names/asmdefs, preserved + AOT-patch assemblies, output dirs, iteration limits, repo URLs) and persist to ProjectSettings/HybridCLRSettings.asset. Only the parameters you pass are changed; the full prior settings object is snapshotted for workflow undo.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Modify,
            Tags = new[] { "hybridclr", "settings", "config", "assemblies", "write" },
            Outputs = new[] { "changed", "settings", "unresolvedAssemblyDefinitions" },
            RequiresPackages = new[] { PackageId },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesAssets = true, RiskLevel = "medium")]
        public static object SettingsSet(
            bool? enable = null,
            bool? useGlobalIl2cpp = null,
            string[] hotUpdateAssemblies = null,
            string[] hotUpdateAssemblyDefinitions = null,
            string[] preserveHotUpdateAssemblies = null,
            string[] patchAOTAssemblies = null,
            string[] externalHotUpdateAssemblyDirs = null,
            string hotUpdateDllCompileOutputRootDir = null,
            string strippedAOTDllOutputRootDir = null,
            string outputLinkFile = null,
            string outputAOTGenericReferenceFile = null,
            int? maxGenericReferenceIteration = null,
            int? maxMethodBridgeGenericIteration = null,
            string hybridclrRepoURL = null,
            string il2cppPlusRepoURL = null)
        {
            if (!Installed) return NoHybridCLR();

            var settings = SettingsInstance();
            if (settings == null) return MissingApi("HybridCLR.Editor.Settings.HybridCLRSettings.Instance");

            var before = CaptureSettings();
            if (before == null) return MissingApi("HybridCLRSettings public fields");

            if (maxGenericReferenceIteration.HasValue &&
                Validate.InRange(maxGenericReferenceIteration.Value, 1, 20, "maxGenericReferenceIteration") is object genErr)
                return genErr;
            if (maxMethodBridgeGenericIteration.HasValue &&
                Validate.InRange(maxMethodBridgeGenericIteration.Value, 1, 20, "maxMethodBridgeGenericIteration") is object bridgeErr)
                return bridgeErr;

            var unresolved = new List<string>();
            if (hotUpdateAssemblyDefinitions != null)
            {
                foreach (var p in hotUpdateAssemblyDefinitions)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    if (Validate.SafePath(p, "hotUpdateAssemblyDefinitions") is object pathErr) return pathErr;
                    if (!p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                        return new { error = $"hotUpdateAssemblyDefinitions entries must be .asmdef asset paths; got '{p}'." };
                }
            }

            if (WorkflowManager.IsRecording)
            {
                WorkflowManager.SnapshotSetting(SettingsKey,
                    JsonConvert.SerializeObject(before),
                    "HybridCLR: Settings");
            }

            // In-memory revert for Ctrl+Z. The file-side revert comes from the workflow restorer —
            // this object lives in ProjectSettings/ and is written by HybridCLRSettings.Save(),
            // which Unity's undo stack does not drive.
            if (settings is UnityEngine.Object settingsObject)
                Undo.RegisterCompleteObjectUndo(settingsObject, "HybridCLR Settings");

            var changed = new List<string>();

            void SetField(string field, object value, string label)
            {
                var f = SettingsField(field);
                if (f == null)
                {
                    SkillsLogger.LogWarning($"[HybridCLR] settings field '{field}' not found on this package version — skipped.");
                    return;
                }
                f.SetValue(settings, value);
                changed.Add(label);
            }

            if (enable.HasValue) SetField("enable", enable.Value, "enable");
            if (useGlobalIl2cpp.HasValue) SetField("useGlobalIl2cpp", useGlobalIl2cpp.Value, "useGlobalIl2cpp");
            if (hotUpdateAssemblies != null) SetField("hotUpdateAssemblies", CleanNames(hotUpdateAssemblies), "hotUpdateAssemblies");
            if (preserveHotUpdateAssemblies != null) SetField("preserveHotUpdateAssemblies", CleanNames(preserveHotUpdateAssemblies), "preserveHotUpdateAssemblies");
            if (patchAOTAssemblies != null) SetField("patchAOTAssemblies", CleanNames(patchAOTAssemblies), "patchAOTAssemblies");
            if (externalHotUpdateAssemblyDirs != null) SetField("externalHotUpdateAssembliyDirs", CleanNames(externalHotUpdateAssemblyDirs), "externalHotUpdateAssembliyDirs");
            if (hotUpdateDllCompileOutputRootDir != null) SetField("hotUpdateDllCompileOutputRootDir", hotUpdateDllCompileOutputRootDir, "hotUpdateDllCompileOutputRootDir");
            if (strippedAOTDllOutputRootDir != null) SetField("strippedAOTDllOutputRootDir", strippedAOTDllOutputRootDir, "strippedAOTDllOutputRootDir");
            if (outputLinkFile != null) SetField("outputLinkFile", outputLinkFile, "outputLinkFile");
            if (outputAOTGenericReferenceFile != null) SetField("outputAOTGenericReferenceFile", outputAOTGenericReferenceFile, "outputAOTGenericReferenceFile");
            if (maxGenericReferenceIteration.HasValue) SetField("maxGenericReferenceIteration", maxGenericReferenceIteration.Value, "maxGenericReferenceIteration");
            if (maxMethodBridgeGenericIteration.HasValue) SetField("maxMethodBridgeGenericIteration", maxMethodBridgeGenericIteration.Value, "maxMethodBridgeGenericIteration");
            if (hybridclrRepoURL != null) SetField("hybridclrRepoURL", hybridclrRepoURL, "hybridclrRepoURL");
            if (il2cppPlusRepoURL != null) SetField("il2cppPlusRepoURL", il2cppPlusRepoURL, "il2cppPlusRepoURL");

            if (hotUpdateAssemblyDefinitions != null)
            {
                AssignAsmdefs(settings, hotUpdateAssemblyDefinitions, out unresolved);
                changed.Add("hotUpdateAssemblyDefinitions");
            }

            if (changed.Count == 0)
            {
                return new
                {
                    changed = Array.Empty<string>(),
                    settings = before,
                    note = "No parameters supplied — nothing was written. Pass at least one field to change."
                };
            }

            if (!SaveSettings())
                return new { error = "HybridCLRSettings.Save() could not be invoked; changes are in memory only and will be lost on domain reload." };

            var after = CaptureSettings();
            SkillsLogger.Log($"[HybridCLR] settings updated: {string.Join(", ", changed)}");

            return new
            {
                changed = changed.ToArray(),
                settings = after,
                unresolvedAssemblyDefinitions = unresolved.ToArray(),
                settingsAssetPath = "ProjectSettings/HybridCLRSettings.asset",
                warning = unresolved.Count > 0
                    ? $"{unresolved.Count} asmdef path(s) could not be loaded and were dropped from hotUpdateAssemblyDefinitions."
                    : null
            };
        }

        private static string[] CleanNames(string[] values) =>
            (values ?? Array.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .ToArray();

        // ==================================================================================
        // C. Diagnostics (1 skill)
        // ==================================================================================

        [UnitySkill("hybridclr_validate_setup",
            "Pre-flight check of the whole HybridCLR setup for a build target — package present, enable flag, IL2CPP backend, libil2cpp patched and version-matched, hot-update assemblies configured and resolvable, generated artifacts present and newer than the last compile. Returns categorised errors and warnings with fixes.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Analyze,
            Tags = new[] { "hybridclr", "validate", "diagnose", "preflight", "check" },
            Outputs = new[] { "ok", "errors", "warnings", "checks" },
            ReadOnly = true,
            RequiresPackages = new[] { PackageId },
            Mode = SkillMode.SemiAuto)]
        public static object ValidateSetup(string buildTarget = null)
        {
            if (!Installed) return NoHybridCLR();
            if (!TryParseBuildTarget(buildTarget, out var target, out var targetError)) return targetError;

            var errors = new List<object>();
            var warnings = new List<object>();
            var checks = new List<object>();

            void Check(string id, bool passed, string detail, string fix)
            {
                checks.Add(new { id, passed, detail });
                if (!passed) errors.Add(new { id, detail, fix });
            }

            void Warn(string id, bool passed, string detail, string advice)
            {
                checks.Add(new { id, passed, detail });
                if (!passed) warnings.Add(new { id, detail, fix = advice });
            }

            var settings = SettingsInstance();
            if (settings == null)
                return MissingApi("HybridCLR.Editor.Settings.HybridCLRSettings.Instance");

            bool enabled = (StaticProp(SettingsUtilType, "Enable") as bool?) ?? false;
            Check("enable", enabled,
                $"HybridCLRSettings.enable = {enabled}",
                "Call hybridclr_settings_set with enable=true.");

            var backend = ActiveScriptingBackend();
            bool isIl2cpp = string.Equals(backend, "IL2CPP", StringComparison.OrdinalIgnoreCase);
            Check("scriptingBackend", isIl2cpp,
                $"Scripting backend for the active build target is {backend}",
                "HybridCLR requires IL2CPP. Set it in Project Settings > Player > Other Settings > Scripting Backend.");

            var installer = new InstallerHandle();
            bool hasInstalled = installer.HasInstalledHybridCLR() ?? false;
            Check("il2cppInstalled", hasInstalled,
                hasInstalled ? "libil2cpp is patched with hybridclr." : "libil2cpp has NOT been patched.",
                "Open HybridCLR/Installer... in the Editor and run the install once (clones two git repos).");

            if (hasInstalled)
            {
                var pkgVersion = installer.StringMember("PackageVersion");
                var installedVersion = installer.StringMember("InstalledLibil2cppVersion")?.Trim();
                Warn("libil2cppVersionMatch",
                    !string.IsNullOrEmpty(pkgVersion) && string.Equals(pkgVersion, installedVersion, StringComparison.Ordinal),
                    $"package {pkgVersion ?? "?"} vs installed libil2cpp {installedVersion ?? "?"}",
                    "Re-run HybridCLR/Installer... — the package was upgraded after the last install.");
            }

            var hotUpdateNames = StaticPropStringList(SettingsUtilType, "HotUpdateAssemblyNamesExcludePreserved", out var hotUpdateError);
            if (!string.IsNullOrEmpty(hotUpdateError))
            {
                errors.Add(new
                {
                    id = "hotUpdateAssemblyResolve",
                    detail = hotUpdateError,
                    fix = "Fix the duplicate/invalid entry via hybridclr_settings_set."
                });
            }
            Check("hotUpdateAssembliesConfigured", hotUpdateNames != null && hotUpdateNames.Count > 0,
                $"{hotUpdateNames?.Count ?? 0} hot-update assemblies configured",
                "Add asmdefs or names via hybridclr_settings_set(hotUpdateAssemblyDefinitions=[...]) — nothing can be hot-updated otherwise.");

            var asmdefField = SettingsField("hotUpdateAssemblyDefinitions");
            var brokenAsmdefs = (asmdefField?.GetValue(settings) as UnityEngine.Object[])
                ?.Select((o, i) => new { o, i })
                .Where(x => x.o == null)
                .Select(x => x.i)
                .ToArray() ?? Array.Empty<int>();
            Check("assemblyDefinitionsResolvable", brokenAsmdefs.Length == 0,
                brokenAsmdefs.Length == 0 ? "All asmdef references resolve." : $"{brokenAsmdefs.Length} null entries at index {string.Join(", ", brokenAsmdefs)}",
                "A referenced asmdef asset was deleted or moved. Re-set hotUpdateAssemblyDefinitions.");

            var aotNames = StaticPropStringList(SettingsUtilType, "AOTAssemblyNames", out _);
            Warn("patchAOTAssemblies", (aotNames?.Count ?? 0) > 0,
                $"{aotNames?.Count ?? 0} AOT assemblies listed for metadata supplementation",
                "Run hybridclr_generate_step(step=\"aot_generic_reference\") and copy PatchedAOTAssemblyList into patchAOTAssemblies, or generic instantiations will throw at runtime.");

            var linkXml = LinkXmlPath();
            var aotRefs = AotGenericReferencesPath();
            Warn("linkXmlGenerated", !string.IsNullOrEmpty(linkXml) && File.Exists(linkXml),
                $"link.xml: {(string.IsNullOrEmpty(linkXml) ? "not configured" : ToProjectRelative(linkXml))}",
                "Run hybridclr_generate_all or hybridclr_generate_step(step=\"link_xml\").");
            Warn("aotGenericReferencesGenerated", !string.IsNullOrEmpty(aotRefs) && File.Exists(aotRefs),
                $"AOTGenericReferences.cs: {(string.IsNullOrEmpty(aotRefs) ? "not configured" : ToProjectRelative(aotRefs))}",
                "Run hybridclr_generate_all or hybridclr_generate_step(step=\"aot_generic_reference\").");

            var dllDir = ResolveProjectPath(HotUpdateDllsDirFor(target));
            var expectedFiles = StaticPropStringList(SettingsUtilType, "HotUpdateAssemblyFilesIncludePreserved", out _) ?? new List<string>();
            var missingDlls = expectedFiles
                .Where(f => string.IsNullOrEmpty(dllDir) || !File.Exists(Path.Combine(dllDir, f)))
                .ToArray();
            Warn("hotUpdateDllsCompiled", expectedFiles.Count > 0 && missingDlls.Length == 0,
                missingDlls.Length == 0
                    ? $"All {expectedFiles.Count} hot-update DLLs present in {ToProjectRelative(dllDir)}"
                    : $"Missing in {ToProjectRelative(dllDir)}: {string.Join(", ", missingDlls)}",
                $"Run hybridclr_compile_dlls(buildTarget=\"{target}\").");

            return new
            {
                ok = errors.Count == 0,
                buildTarget = target.ToString(),
                errorCount = errors.Count,
                warningCount = warnings.Count,
                errors = errors.ToArray(),
                warnings = warnings.ToArray(),
                checks = checks.ToArray(),
                docs = DocsUrl
            };
        }

        private static string ActiveScriptingBackend()
        {
            try
            {
                var target = EditorUserBuildSettings.activeBuildTarget;
                var group = BuildPipeline.GetBuildTargetGroup(target);
                return PlayerSettings
                    .GetScriptingBackend(UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(group))
                    .ToString();
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[HybridCLR] scripting backend probe failed: {ex.Message}");
                return "unknown";
            }
        }

        // ==================================================================================
        // D. Compile & generate (3 skills) — long-running, main-thread blocking
        // ==================================================================================

        [UnitySkill("hybridclr_compile_dlls",
            "Compile the hot-update assemblies for a build target via HybridCLR.Editor.Commands.CompileDllCommand.CompileDll(target, developmentBuild). Writes DLL/PDB into HybridCLRData/HotUpdateDlls/<target>. BLOCKS the Editor main thread for the duration of a full player-script compile (tens of seconds to minutes on large projects); prior output is backed up for workflow undo.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Execute,
            Tags = new[] { "hybridclr", "compile", "hotupdate", "dll", "build" },
            Outputs = new[] { "success", "buildTarget", "outputDir", "files", "elapsedSeconds" },
            RequiresPackages = new[] { PackageId },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesAssets = true, SupportsDryRun = false, RiskLevel = "high")]
        public static object CompileDlls(string buildTarget = null, bool developmentBuild = false)
        {
            if (!Installed) return NoHybridCLR();
            if (!TryParseBuildTarget(buildTarget, out var target, out var targetError)) return targetError;

            var commandType = HclrType("HybridCLR.Editor.Commands.CompileDllCommand");
            if (commandType == null) return MissingApi("HybridCLR.Editor.Commands.CompileDllCommand");

            var method = commandType.GetMethod("CompileDll",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(BuildTarget), typeof(bool) }, null);
            if (method == null) return MissingApi("CompileDllCommand.CompileDll(BuildTarget, bool)");

            if (EditorApplication.isCompiling)
                return new { error = "Unity is still compiling scripts. Wait for compilation to finish before compiling hot-update DLLs." };

            if (BuildPipeline.isBuildingPlayer)
                return new { error = "A player build is already in progress. Wait for it to finish before compiling hot-update DLLs." };

            var outputDir = ResolveProjectPath(HotUpdateDllsDirFor(target));
            if (string.IsNullOrEmpty(outputDir))
                return MissingApi("SettingsUtil.GetHotUpdateDllsOutputDirByTarget(BuildTarget)");

            if (WorkflowManager.IsRecording)
            {
                var backup = CaptureFileSet("hotUpdateDlls", outputDir, refreshAssetDatabase: false);
                WorkflowManager.SnapshotSetting(FileSetKey,
                    JsonConvert.SerializeObject(backup),
                    $"HybridCLR: Compile hot-update DLLs ({target})");
            }

            var started = DateTime.UtcNow;
            try
            {
                method.Invoke(null, new object[] { target, developmentBuild });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return new
                {
                    success = false,
                    buildTarget = target.ToString(),
                    error = inner.Message,
                    exceptionType = inner.GetType().Name,
                    hint = "Hot-update assemblies must compile cleanly for the target platform. Check the Console for the underlying compiler errors."
                };
            }
            finally
            {
                // CompileDllCommand only clears the progress bar on Unity 2022; do it unconditionally
                // so a failure part-way through cannot leave the Editor with a stuck bar.
                try { EditorUtility.ClearProgressBar(); } catch { }
            }

            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            SkillsLogger.Log($"[HybridCLR] compiled hot-update DLLs for {target} in {elapsed:F1}s");

            return new
            {
                success = true,
                buildTarget = target.ToString(),
                developmentBuild,
                outputDir,
                projectRelativeOutputDir = ToProjectRelative(outputDir),
                elapsedSeconds = Math.Round(elapsed, 2),
                files = ListDllArtifacts(outputDir),
                next = "hybridclr_get_hotupdate_dlls to inspect artifacts, then hybridclr_copy_hotupdate_dlls to stage them for YooAsset."
            };
        }

        [UnitySkill("hybridclr_generate_all",
            "Run the full HybridCLR prebuild pipeline via HybridCLR.Editor.Commands.PrebuildCommand.GenerateAll() — compile hot-update DLLs, Il2CppDef, link.xml, stripped AOT DLLs, method bridges, and AOTGenericReferences.cs, in that order, for the active build target. BLOCKS the Editor main thread for several minutes and rewrites generated C# under Assets/, which triggers a domain reload.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Execute,
            Tags = new[] { "hybridclr", "generate", "prebuild", "pipeline", "build" },
            Outputs = new[] { "success", "buildTarget", "elapsedSeconds", "generatedArtifacts" },
            RequiresPackages = new[] { PackageId },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesAssets = true, MayTriggerReload = true, SupportsDryRun = false, RiskLevel = "high")]
        public static object GenerateAll()
        {
            if (!Installed) return NoHybridCLR();

            var commandType = HclrType("HybridCLR.Editor.Commands.PrebuildCommand");
            if (commandType == null) return MissingApi("HybridCLR.Editor.Commands.PrebuildCommand");

            var method = commandType.GetMethod("GenerateAll",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (method == null) return MissingApi("PrebuildCommand.GenerateAll()");

            if (EditorApplication.isCompiling)
                return new { error = "Unity is still compiling scripts. Wait for compilation to finish before running the prebuild pipeline." };

            // The aot_dlls stage inside GenerateAll runs a scripts-only BuildPipeline.BuildPlayer,
            // which throws if another build is already running.
            if (BuildPipeline.isBuildingPlayer)
                return new { error = "A player build is already in progress. GenerateAll runs its own scripts-only player build internally and cannot be nested." };

            var target = EditorUserBuildSettings.activeBuildTarget;
            var outputDir = ResolveProjectPath(HotUpdateDllsDirFor(target));

            if (WorkflowManager.IsRecording)
            {
                WorkflowManager.SnapshotSetting(GeneratedSourcesKey,
                    JsonConvert.SerializeObject(CaptureGeneratedSources()),
                    "HybridCLR: Generate All (Assets-side generated sources)");

                if (!string.IsNullOrEmpty(outputDir))
                {
                    WorkflowManager.SnapshotSetting(FileSetKey,
                        JsonConvert.SerializeObject(CaptureFileSet("hotUpdateDlls", outputDir, refreshAssetDatabase: false)),
                        $"HybridCLR: Generate All (hot-update DLLs, {target})");
                }
            }

            var started = DateTime.UtcNow;
            try
            {
                method.Invoke(null, null);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return new
                {
                    success = false,
                    buildTarget = target.ToString(),
                    error = inner.Message,
                    exceptionType = inner.GetType().Name,
                    hint = inner.GetType().Name == "BuildFailedException"
                        ? "GenerateAll refuses to run until libil2cpp is patched — open HybridCLR/Installer... first. Check hybridclr_install_status."
                        : "Run hybridclr_validate_setup to locate the misconfiguration, then retry."
                };
            }
            finally
            {
                try { EditorUtility.ClearProgressBar(); } catch { }
            }

            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            SkillsLogger.Log($"[HybridCLR] GenerateAll completed for {target} in {elapsed:F1}s");

            return new
            {
                success = true,
                buildTarget = target.ToString(),
                elapsedSeconds = Math.Round(elapsed, 2),
                steps = new[] { "compile_dll", "il2cpp_def", "link_xml", "aot_dlls", "method_bridge", "aot_generic_reference" },
                generatedArtifacts = new
                {
                    linkXml = FileInfoPayload(LinkXmlPath()),
                    aotGenericReferences = FileInfoPayload(AotGenericReferencesPath()),
                    hotUpdateDlls = ListDllArtifacts(outputDir)
                },
                warning = "AOTGenericReferences.cs was rewritten under Assets/ — a domain reload is imminent and the REST server will be briefly unreachable.",
                next = "Copy PatchedAOTAssemblyList from hybridclr_aot_generic_refs into settings.patchAOTAssemblies, then rebuild the player."
            };
        }

        // Step order matches PrebuildCommand.GenerateAll. "aot_dlls" is the expensive one: it runs a
        // scripts-only BuildPipeline.BuildPlayer into a temp project and temporarily flips several
        // EditorUserBuildSettings flags, restoring them in a finally block.
        private static readonly Dictionary<string, (string Type, string Method, bool TakesTarget, bool TouchesAssets)> GenerateSteps =
            new Dictionary<string, (string Type, string Method, bool TakesTarget, bool TouchesAssets)>(StringComparer.OrdinalIgnoreCase)
            {
                ["il2cpp_def"]            = ("HybridCLR.Editor.Commands.Il2CppDefGeneratorCommand", "GenerateIl2CppDef", false, false),
                ["link_xml"]              = ("HybridCLR.Editor.Commands.LinkGeneratorCommand", "GenerateLinkXml", true, true),
                ["aot_dlls"]              = ("HybridCLR.Editor.Commands.StripAOTDllCommand", "GenerateStripedAOTDlls", true, false),
                ["method_bridge"]         = ("HybridCLR.Editor.Commands.MethodBridgeGeneratorCommand", "GenerateMethodBridgeAndReversePInvokeWrapper", true, false),
                ["aot_generic_reference"] = ("HybridCLR.Editor.Commands.AOTReferenceGeneratorCommand", "GenerateAOTGenericReference", true, true),
                ["clean_il2cpp_cache"]    = ("HybridCLR.Editor.Commands.MethodBridgeGeneratorCommand", "CleanIl2CppBuildCache", false, false),
            };

        [UnitySkill("hybridclr_generate_step",
            "Run a single step of the HybridCLR prebuild pipeline instead of the whole thing: il2cpp_def, link_xml, aot_dlls, method_bridge, aot_generic_reference, or clean_il2cpp_cache. Steps that write C#/XML under Assets/ trigger an AssetDatabase refresh and may cause a domain reload. Assumes hot-update DLLs are already compiled — run hybridclr_compile_dlls first.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Execute,
            Tags = new[] { "hybridclr", "generate", "step", "linkxml", "methodbridge", "aot" },
            Outputs = new[] { "success", "step", "buildTarget", "elapsedSeconds" },
            RequiresInput = new[] { "step" },
            RequiresPackages = new[] { PackageId },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesAssets = true, MayTriggerReload = true, SupportsDryRun = false, RiskLevel = "high")]
        public static object GenerateStep(string step, string buildTarget = null)
        {
            if (!Installed) return NoHybridCLR();
            if (Validate.Required(step, "step") is object stepErr) return stepErr;

            if (!GenerateSteps.TryGetValue(step.Trim(), out var spec))
            {
                return new
                {
                    error = $"Unknown step '{step}'.",
                    available = GenerateSteps.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
                    hint = "Use hybridclr_generate_all to run every step in the correct order."
                };
            }

            if (!TryParseBuildTarget(buildTarget, out var target, out var targetError)) return targetError;

            var commandType = HclrType(spec.Type);
            if (commandType == null) return MissingApi(spec.Type);

            var paramTypes = spec.TakesTarget ? new[] { typeof(BuildTarget) } : Type.EmptyTypes;
            var method = commandType.GetMethod(spec.Method, BindingFlags.Public | BindingFlags.Static, null, paramTypes, null);
            if (method == null)
                return MissingApi($"{spec.Type}.{spec.Method}({(spec.TakesTarget ? "BuildTarget" : "")})");

            if (EditorApplication.isCompiling)
                return new { error = "Unity is still compiling scripts. Wait for compilation to finish before running a generation step." };

            if (BuildPipeline.isBuildingPlayer)
                return new { error = "A player build is already in progress. Wait for it to finish before running a generation step." };

            if (spec.TouchesAssets && WorkflowManager.IsRecording)
            {
                WorkflowManager.SnapshotSetting(GeneratedSourcesKey,
                    JsonConvert.SerializeObject(CaptureGeneratedSources()),
                    $"HybridCLR: Generate step '{step}'");
            }

            var started = DateTime.UtcNow;
            try
            {
                method.Invoke(null, spec.TakesTarget ? new object[] { target } : null);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return new
                {
                    success = false,
                    step,
                    buildTarget = target.ToString(),
                    error = inner.Message,
                    exceptionType = inner.GetType().Name,
                    hint = "Most step failures mean the hot-update DLLs are stale or missing. Run hybridclr_compile_dlls, then retry."
                };
            }
            finally
            {
                try { EditorUtility.ClearProgressBar(); } catch { }
            }

            var elapsed = (DateTime.UtcNow - started).TotalSeconds;

            return new
            {
                success = true,
                step,
                buildTarget = spec.TakesTarget ? target.ToString() : null,
                elapsedSeconds = Math.Round(elapsed, 2),
                touchesAssets = spec.TouchesAssets,
                linkXml = FileInfoPayload(LinkXmlPath()),
                aotGenericReferences = FileInfoPayload(AotGenericReferencesPath()),
                warning = spec.TouchesAssets
                    ? "This step called AssetDatabase.Refresh(); a domain reload may follow and the REST server will be briefly unreachable."
                    : null
            };
        }

        // ==================================================================================
        // E. Artifacts (3 skills)
        // ==================================================================================

        [UnitySkill("hybridclr_get_hotupdate_dlls",
            "List the compiled hot-update DLL artifacts for a build target with size and UTC timestamp, and reconcile them against the configured hot-update assembly list so missing or stale outputs are obvious. Use before staging DLLs into a YooAsset collector directory.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Query,
            Tags = new[] { "hybridclr", "dll", "artifacts", "hotupdate", "yooasset" },
            Outputs = new[] { "outputDir", "files", "expectedAssemblies", "missing", "unexpected" },
            ReadOnly = true,
            RequiresPackages = new[] { PackageId },
            Mode = SkillMode.SemiAuto)]
        public static object GetHotUpdateDlls(string buildTarget = null)
        {
            if (!Installed) return NoHybridCLR();
            if (!TryParseBuildTarget(buildTarget, out var target, out var targetError)) return targetError;

            var outputDir = ResolveProjectPath(HotUpdateDllsDirFor(target));
            if (string.IsNullOrEmpty(outputDir))
                return MissingApi("SettingsUtil.GetHotUpdateDllsOutputDirByTarget(BuildTarget)");

            var expected = StaticPropStringList(SettingsUtilType, "HotUpdateAssemblyFilesIncludePreserved", out var expectedError)
                           ?? new List<string>();
            var artifacts = ListDllArtifacts(outputDir);
            var presentNames = new HashSet<string>(
                artifacts.Select(a => a.name).Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

            return new
            {
                buildTarget = target.ToString(),
                outputDir,
                projectRelativeOutputDir = ToProjectRelative(outputDir),
                exists = Directory.Exists(outputDir),
                fileCount = artifacts.Length,
                files = artifacts,
                expectedAssemblies = expected.ToArray(),
                expectedResolveError = expectedError,
                missing = expected.Where(f => !presentNames.Contains(f)).ToArray(),
                unexpected = presentNames.Where(n => !expected.Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray(),
                note = "Unity will not import a bare .dll under Assets/ as data — stage it with hybridclr_copy_hotupdate_dlls (default extension .bytes) before adding it to a YooAsset collector."
            };
        }

        private sealed class DllArtifact
        {
            public string name;
            public string path;
            public long sizeBytes;
            public string lastWriteUtc;
        }

        private static DllArtifact[] ListDllArtifacts(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return Array.Empty<DllArtifact>();

            try
            {
                return Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .Select(f =>
                    {
                        var fi = new FileInfo(f);
                        return new DllArtifact
                        {
                            name = fi.Name,
                            path = Normalize(f),
                            sizeBytes = fi.Length,
                            lastWriteUtc = fi.LastWriteTimeUtc.ToString("o")
                        };
                    })
                    .ToArray();
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[HybridCLR] could not enumerate '{dir}': {ex.Message}");
                return Array.Empty<DllArtifact>();
            }
        }

        [UnitySkill("hybridclr_copy_hotupdate_dlls",
            "Stage compiled hot-update DLLs (and optionally the stripped AOT DLLs) into a directory under Assets/, renaming them to an importable extension (default .bytes) so a YooAsset collector can pack them. Existing files in the destination are backed up for workflow undo.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Create,
            Tags = new[] { "hybridclr", "copy", "stage", "yooasset", "bytes", "hotupdate" },
            Outputs = new[] { "destination", "copied", "skipped" },
            RequiresInput = new[] { "destination" },
            RequiresPackages = new[] { PackageId },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesAssets = true, RiskLevel = "medium")]
        public static object CopyHotUpdateDlls(
            string destination,
            string buildTarget = null,
            string extension = ".bytes",
            string[] assemblies = null,
            bool includeAotAssemblies = false,
            bool clearDestination = false)
        {
            if (!Installed) return NoHybridCLR();
            if (Validate.Required(destination, "destination") is object destErr) return destErr;
            if (Validate.SafePath(destination, "destination") is object safeErr) return safeErr;
            if (!TryParseBuildTarget(buildTarget, out var target, out var targetError)) return targetError;

            var normalizedDestination = destination.Replace('\\', '/').TrimEnd('/');
            if (!normalizedDestination.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !normalizedDestination.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    error = $"destination must be under Assets/ so Unity can import the staged files; got '{destination}'.",
                    hint = "Example: Assets/HotUpdateDlls"
                };
            }

            var ext = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension.Trim();
            if (ext.Length > 0 && !ext.StartsWith("."))
                ext = "." + ext;
            if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    error = "extension=\".dll\" would make Unity treat the staged files as managed plugins and try to load them into the Editor domain.",
                    hint = "Use \".bytes\" (default) so they import as TextAsset, which is what a YooAsset collector expects."
                };
            }

            var sourceDir = ResolveProjectPath(HotUpdateDllsDirFor(target));
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
            {
                return new
                {
                    error = $"Hot-update DLL directory does not exist: {ToProjectRelative(sourceDir) ?? "(unresolved)"}",
                    hint = $"Run hybridclr_compile_dlls(buildTarget=\"{target}\") first."
                };
            }

            var wanted = StaticPropStringList(SettingsUtilType, "HotUpdateAssemblyFilesIncludePreserved", out _) ?? new List<string>();
            if (assemblies != null && assemblies.Length > 0)
            {
                wanted = CleanNames(assemblies)
                    .Select(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? a : a + ".dll")
                    .ToList();
            }

            var sources = new List<string>();
            var skipped = new List<object>();
            foreach (var fileName in wanted)
            {
                var src = Path.Combine(sourceDir, fileName);
                if (File.Exists(src)) sources.Add(src);
                else skipped.Add(new { name = fileName, reason = "not found in hot-update DLL output" });
            }

            if (includeAotAssemblies)
            {
                var aotDir = ResolveProjectPath(StrippedAotDirFor(target));
                var aotNames = StaticPropStringList(SettingsUtilType, "AOTAssemblyNames", out _) ?? new List<string>();
                foreach (var name in aotNames)
                {
                    var fileName = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
                    var src = string.IsNullOrEmpty(aotDir) ? null : Path.Combine(aotDir, fileName);
                    if (!string.IsNullOrEmpty(src) && File.Exists(src)) sources.Add(src);
                    else skipped.Add(new { name = fileName, reason = "not found in stripped AOT output — run hybridclr_generate_step(step=\"aot_dlls\")" });
                }
            }

            if (sources.Count == 0)
            {
                return new
                {
                    error = "Nothing to copy — none of the requested assemblies exist in the compiled output.",
                    sourceDir = ToProjectRelative(sourceDir),
                    skipped = skipped.ToArray(),
                    hint = $"Run hybridclr_compile_dlls(buildTarget=\"{target}\") and check hybridclr_get_hotupdate_dlls."
                };
            }

            var absoluteDestination = Normalize(Path.Combine(ProjectRoot(), normalizedDestination));

            if (WorkflowManager.IsRecording)
            {
                WorkflowManager.SnapshotSetting(FileSetKey,
                    JsonConvert.SerializeObject(CaptureFileSet("stagedDlls", absoluteDestination, refreshAssetDatabase: true)),
                    $"HybridCLR: Stage hot-update DLLs into {normalizedDestination}");
            }

            try
            {
                if (clearDestination && Directory.Exists(absoluteDestination))
                {
                    foreach (var f in Directory.GetFiles(absoluteDestination, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                        File.Delete(f);
                        var meta = f + ".meta";
                        if (File.Exists(meta)) File.Delete(meta);
                    }
                }

                Directory.CreateDirectory(absoluteDestination);

                var copied = new List<object>();
                foreach (var src in sources)
                {
                    var targetName = Path.GetFileNameWithoutExtension(src) + (ext.Length > 0 ? ext : Path.GetExtension(src));
                    var dst = Path.Combine(absoluteDestination, targetName);
                    File.Copy(src, dst, true);
                    copied.Add(new
                    {
                        source = ToProjectRelative(Normalize(src)),
                        destination = $"{normalizedDestination}/{targetName}",
                        sizeBytes = new FileInfo(dst).Length
                    });
                }

                AssetDatabase.Refresh();
                SkillsLogger.Log($"[HybridCLR] staged {copied.Count} hot-update file(s) into {normalizedDestination}");

                return new
                {
                    success = true,
                    buildTarget = target.ToString(),
                    destination = normalizedDestination,
                    extension = ext,
                    copiedCount = copied.Count,
                    copied = copied.ToArray(),
                    skipped = skipped.ToArray(),
                    next = "Point a YooAsset collector at this directory (yooasset_add_collector), then run yooasset_build_bundles."
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    success = false,
                    error = ex.Message,
                    exceptionType = ex.GetType().Name,
                    destination = normalizedDestination
                };
            }
        }

        [UnitySkill("hybridclr_aot_generic_refs",
            "Read the generated AOTGenericReferences.cs — whether it exists, when it was written, and the PatchedAOTAssemblyList it declares, compared against the patchAOTAssemblies currently configured in HybridCLRSettings. Optionally return the raw file content.",
            Category = SkillCategory.HybridCLR, Operation = SkillOperation.Query,
            Tags = new[] { "hybridclr", "aot", "generic", "metadata", "patch" },
            Outputs = new[] { "exists", "patchedAOTAssemblyList", "configuredPatchAOTAssemblies", "inSync", "genericTypeCount" },
            ReadOnly = true,
            RequiresPackages = new[] { PackageId },
            Mode = SkillMode.SemiAuto)]
        public static object AotGenericRefs(bool includeContent = false)
        {
            if (!Installed) return NoHybridCLR();

            var settings = SettingsInstance();
            if (settings == null) return MissingApi("HybridCLR.Editor.Settings.HybridCLRSettings.Instance");

            var absolute = AotGenericReferencesPath();
            if (string.IsNullOrEmpty(absolute))
                return new { error = "HybridCLRSettings.outputAOTGenericReferenceFile is empty — AOTGenericReferences generation is disabled." };

            var configured = StaticPropStringList(SettingsUtilType, "AOTAssemblyNames", out _) ?? new List<string>();

            if (!File.Exists(absolute))
            {
                return new
                {
                    exists = false,
                    path = absolute,
                    projectRelativePath = ToProjectRelative(absolute),
                    configuredPatchAOTAssemblies = configured.ToArray(),
                    hint = "Run hybridclr_generate_step(step=\"aot_generic_reference\") or hybridclr_generate_all to produce it."
                };
            }

            string content;
            try { content = File.ReadAllText(absolute); }
            catch (Exception ex)
            {
                return new { error = $"Could not read '{absolute}': {ex.Message}" };
            }

            var patched = ParsePatchedAotAssemblyList(content);
            var fi = new FileInfo(absolute);

            // HybridCLR writes patchAOTAssemblies without the .dll suffix but emits module names
            // (which carry it) into PatchedAOTAssemblyList, so compare on the stem.
            string Stem(string s) => s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 4) : s;
            var patchedStems = new HashSet<string>(patched.Select(Stem), StringComparer.OrdinalIgnoreCase);
            var configuredStems = new HashSet<string>(configured.Select(Stem), StringComparer.OrdinalIgnoreCase);

            return new
            {
                exists = true,
                path = absolute,
                projectRelativePath = ToProjectRelative(absolute),
                lastWriteUtc = fi.LastWriteTimeUtc.ToString("o"),
                sizeBytes = fi.Length,
                patchedAOTAssemblyList = patched,
                configuredPatchAOTAssemblies = configured.ToArray(),
                inSync = patchedStems.SetEquals(configuredStems),
                missingFromSettings = patchedStems.Except(configuredStems).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                extraInSettings = configuredStems.Except(patchedStems).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                genericTypeCount = CountCommentBlockLines(content, "// {{ AOT generic types"),
                content = includeContent ? content : null,
                note = "inSync=false means the generated list and settings.patchAOTAssemblies disagree — copy missingFromSettings into hybridclr_settings_set(patchAOTAssemblies=[...]) or generic instantiations will throw at runtime."
            };
        }

        private static readonly Regex QuotedEntry = new Regex("\"([^\"]+)\"", RegexOptions.Compiled);

        private static string[] ParsePatchedAotAssemblyList(string content)
        {
            if (string.IsNullOrEmpty(content)) return Array.Empty<string>();

            int start = content.IndexOf("PatchedAOTAssemblyList", StringComparison.Ordinal);
            if (start < 0) return Array.Empty<string>();

            int open = content.IndexOf('{', start);
            if (open < 0) return Array.Empty<string>();

            int close = content.IndexOf("};", open, StringComparison.Ordinal);
            if (close < 0) return Array.Empty<string>();

            return QuotedEntry.Matches(content.Substring(open, close - open))
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToArray();
        }

        /// <summary>
        /// Counts the `// xxx` lines inside one of the writer's `// {{ label` ... `// }}` blocks.
        /// The generator emits generic types as comments only, so this is the sole way to size them.
        /// </summary>
        private static int CountCommentBlockLines(string content, string blockHeader)
        {
            if (string.IsNullOrEmpty(content)) return 0;

            int start = content.IndexOf(blockHeader, StringComparison.Ordinal);
            if (start < 0) return 0;

            int end = content.IndexOf("// }}", start + blockHeader.Length, StringComparison.Ordinal);
            if (end < 0) return 0;

            return content
                .Substring(start + blockHeader.Length, end - start - blockHeader.Length)
                .Split('\n')
                .Count(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal));
        }

        // ==================================================================================
        // InstallerController handle — constructing it reads package metadata off disk and can
        // throw, so every access funnels through here.
        // ==================================================================================

        private sealed class InstallerHandle
        {
            private readonly object _instance;
            internal string Error { get; }

            internal InstallerHandle()
            {
                var type = InstallerControllerType;
                if (type == null)
                {
                    Error = "HybridCLR.Editor.Installer.InstallerController not found";
                    return;
                }

                try { _instance = Activator.CreateInstance(type); }
                catch (Exception ex) { Error = (ex.InnerException ?? ex).Message; }
            }

            internal bool? HasInstalledHybridCLR() => Invoke("HasInstalledHybridCLR") as bool?;

            internal object Invoke(string methodName)
            {
                if (_instance == null) return null;
                try
                {
                    var m = _instance.GetType().GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    return m?.Invoke(_instance, null);
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogVerbose($"[HybridCLR] InstallerController.{methodName}() failed: {(ex.InnerException ?? ex).Message}");
                    return null;
                }
            }

            internal object Member(string propertyName)
            {
                if (_instance == null) return null;
                try
                {
                    var p = _instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    return p?.GetValue(_instance);
                }
                catch (Exception ex)
                {
                    SkillsLogger.LogVerbose($"[HybridCLR] InstallerController.{propertyName} read failed: {(ex.InnerException ?? ex).Message}");
                    return null;
                }
            }

            internal string StringMember(string propertyName) => Member(propertyName) as string;
        }
    }
}

// Producer:Betsy
