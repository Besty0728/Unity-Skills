using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace UnitySkills
{
    /// <summary>
    /// QFramework (https://github.com/liangxiegame/QFramework) editor skills: architecture-layer code generation,
    /// the UIKit/ResKit toolchain, LocaleKit localization, and runtime architecture scanning.
    ///
    /// QFramework can be installed two ways: the Toolkits unitypackage (asmdef: QFramework / QFramework.CoreKit /
    /// UIKit / UIKit.Editor / ResKit / ResKit.Editor / AudioKit), or the single-file QFramework.cs (dropped directly
    /// into Assembly-CSharp, no asmdef, containing only the core architecture interfaces, no Toolkits). This module keeps zero compile-time
    /// dependency on either: every call is resolved via reflection by fully-qualified type name (<see cref="SkillsCommon.FindTypeByName"/>
    /// searches all loaded assemblies and doesn't assume a fixed assembly name), so regardless of whether the project has QFramework installed,
    /// or which form is installed, the UnitySkills editor assembly compiles the same either way. <c>qframework_get_status</c>
    /// is available under all circumstances; every other skill returns a structured MISSING_PACKAGE error when its corresponding capability is missing.
    ///
    /// The APIs used via reflection are based on the QFramework v1.0.257 (liangxiegame/QFramework, 2026-08 snapshot) source code.
    /// </summary>
    public static class QFrameworkSkills
    {
        private const string RequiredPackageDisplay = "QFramework (https://github.com/liangxiegame/QFramework)";
        private const string DocsUrl = "https://qframework.cn";
        private const string CoreArchitectureTypeName = "QFramework.IArchitecture";

        // ArchitectureCodeGenerator's validation/overwrite-rejection messages are hardcoded in Chinese (e.g. "请输入名字" / "文件已存在，
        // QFramework 不会覆盖已有代码：…" and the like), and passing those straight through to a caller working in an English context can easily catch them off guard,
        // so every failure branch attaches a fixed English hint explaining the nature of the message and where to look to fix it.
        private const string UpstreamPreviewValidationHint =
            "Upstream QFramework validation message (may be in Chinese) — check codeType/inputName/namespaceName/outputRoot against ArchitectureCodeGenerator's naming rules (valid C# identifier, not a C# keyword, outputRoot must live under Assets/).";
        private const string UpstreamGenerateOverwriteHint =
            "Upstream QFramework validation message (may be in Chinese); the file-exists error means QFramework refuses to overwrite existing code.";

        private const string UIKitSettingsKey = "qframework.uikitSettings";

        // ==================================================================================
        // Reflection layer -- everything is lazily resolved by fully-qualified name, never statically linked against QFramework.
        // SkillsCommon.FindTypeByName has its own built-in cache (including a miss cache), so this module doesn't need to build a redundant cache.
        // ==================================================================================

        private static Type QType(string fullName) => SkillsCommon.FindTypeByName(fullName);

        private static object StaticGet(Type type, string name)
        {
            if (type == null) return null;
            try
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                if (prop != null) return prop.GetValue(null);
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
                return field?.GetValue(null);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[QFramework] static member '{name}' on {type.FullName} failed: {ex.Message}");
                return null;
            }
        }

        private static bool StaticSet(Type type, string name, object value)
        {
            if (type == null) return false;
            try
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                if (prop != null && prop.CanWrite) { prop.SetValue(null, value); return true; }
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
                if (field != null) { field.SetValue(null, value); return true; }
                return false;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[QFramework] static set '{name}' on {type.FullName} failed: {ex.Message}");
                return false;
            }
        }

        private static object InstanceGet(object instance, string name)
        {
            if (instance == null) return null;
            try
            {
                var type = instance.GetType();
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null) return prop.GetValue(instance);
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                return field?.GetValue(instance);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[QFramework] instance member '{name}' failed: {ex.Message}");
                return null;
            }
        }

        private static bool InstanceSet(object instance, string name, object value)
        {
            if (instance == null) return false;
            try
            {
                var type = instance.GetType();
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite) { prop.SetValue(instance, value); return true; }
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null) { field.SetValue(instance, value); return true; }
                return false;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[QFramework] instance set '{name}' failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Unified error response for a missing package/capability. <paramref name="missingApi"/> is used to pinpoint
        /// exactly which anchor API (type or member) failed to resolve -- more useful than a generic "not installed" when the core is installed but the corresponding Toolkits aren't.
        /// </summary>
        private static object NoQFramework(string missingApi = null) => new
        {
            error = missingApi != null
                ? $"QFramework capability unavailable — anchor API '{missingApi}' could not be resolved by reflection in any loaded assembly."
                : "QFramework is not installed — no 'QFramework.IArchitecture' type could be resolved in any loaded assembly.",
            errorCode = "MISSING_PACKAGE",
            requiredPackage = RequiredPackageDisplay,
            docs = DocsUrl,
            hint = "Call qframework_get_status first — it is the only skill in this module that works without QFramework installed."
        };

        private static bool TryParseCodeType(Type enumType, string value, out object parsed, out object error)
        {
            parsed = null;
            error = null;
            var names = Enum.GetNames(enumType);
            if (!names.Any(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase)))
            {
                error = new
                {
                    error = $"Unknown codeType '{value}'.",
                    errorCode = "SEMANTIC_INVALID",
                    available = names,
                    hint = "Use qframework_list_architecture_code_types to see the available values."
                };
                return false;
            }
            parsed = Enum.Parse(enumType, value, true);
            return true;
        }

        private static string ReadPackageVersion()
        {
            try
            {
                var path = Path.Combine(Application.dataPath, "QFramework/Framework/PackageVersion.json");
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                var obj = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                return obj != null && obj.TryGetValue("Version", out var v) ? v?.ToString() : null;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[QFramework] PackageVersion.json read failed: {ex.Message}");
                return null;
            }
        }

        // ==================================================================================
        // Workflow restorers for ResKit / UIKit / LocaleKit settings (registered on domain load).
        // getter/setter are both lazily reflected; safely does nothing when QFramework isn't installed.
        // ==================================================================================

        [InitializeOnLoadMethod]
        private static void RegisterSettingRestorers()
        {
            WorkflowSettingRestorerRegistry.Register("qframework.simulationMode",
                () => JsonConvert.SerializeObject(GetSimulationMode()),
                json => { SetSimulationModeRaw(JsonConvert.DeserializeObject<bool>(json)); return true; });

            WorkflowSettingRestorerRegistry.Register("qframework.reskitAppendHash",
                () => JsonConvert.SerializeObject(GetResKitEditorPrefBool(AppendHashKey())),
                json => { SetResKitEditorPrefBool(AppendHashKey(), JsonConvert.DeserializeObject<bool>(json)); return true; });

            WorkflowSettingRestorerRegistry.Register("qframework.reskitAutoGenerateClass",
                () => JsonConvert.SerializeObject(GetResKitEditorPrefBool(AutoGenerateClassKey())),
                json => { SetResKitEditorPrefBool(AutoGenerateClassKey(), JsonConvert.DeserializeObject<bool>(json)); return true; });

            WorkflowSettingRestorerRegistry.Register("qframework.editorLocaleIsCN",
                () => JsonConvert.SerializeObject(GetEditorLocaleIsCN()),
                json => { SetEditorLocaleIsCNRaw(JsonConvert.DeserializeObject<bool>(json)); return true; });

            WorkflowSettingRestorerRegistry.Register(UIKitSettingsKey, CaptureUIKitSettingsJson, ApplyUIKitSettingsJson);
        }

        private static bool GetSimulationMode()
        {
            var v = StaticGet(QType("QFramework.ResKitEditorAPI"), "SimulationMode");
            return v is bool b && b;
        }

        private static void SetSimulationModeRaw(bool value) =>
            StaticSet(QType("QFramework.ResKitEditorAPI"), "SimulationMode", value);

        // ResKitView.KEY_APPEND_HASH / KEY_AUTOGENERATE_CLASS are public const strings with no public
        // setter wrapper -- ResKitEditorWindow.EnableGenerateClass only exposes a read-only getter either. Reading and writing EditorPrefs directly against these two
        // constant keys is the only write path that exists in QFramework's own source, not a workaround for the convention.
        private static string AppendHashKey() => StaticGet(QType("QFramework.ResKitView"), "KEY_APPEND_HASH") as string;
        private static string AutoGenerateClassKey() => StaticGet(QType("QFramework.ResKitView"), "KEY_AUTOGENERATE_CLASS") as string;

        private static bool GetResKitEditorPrefBool(string key) => !string.IsNullOrEmpty(key) && EditorPrefs.GetBool(key, false);
        private static void SetResKitEditorPrefBool(string key, bool value) { if (!string.IsNullOrEmpty(key)) EditorPrefs.SetBool(key, value); }

        private static bool GetEditorLocaleIsCN()
        {
            var prop = StaticGet(QType("QFramework.LocaleKitEditor"), "IsCN");
            return InstanceGet(prop, "Value") is bool b && b;
        }

        private static void SetEditorLocaleIsCNRaw(bool value)
        {
            var prop = StaticGet(QType("QFramework.LocaleKitEditor"), "IsCN");
            InstanceSet(prop, "Value", value);
        }

        private sealed class UIKitSettingsSnapshot
        {
            public string Namespace;
            public string UIScriptDir;
            public string UIPrefabDir;
            public List<string> AssemblyNamesToSearch;
        }

        private static object LoadUIKitSettings()
        {
            var settingsType = QType("QFramework.UIKitSettingData");
            return settingsType?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null);
        }

        private static void SaveUIKitSettings(object settings)
        {
            settings?.GetType().GetMethod("Save", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(settings, null);
        }

        private static string CaptureUIKitSettingsJson()
        {
            var settings = LoadUIKitSettings();
            if (settings == null) return null;
            var snap = new UIKitSettingsSnapshot
            {
                Namespace = InstanceGet(settings, "Namespace") as string,
                UIScriptDir = InstanceGet(settings, "UIScriptDir") as string,
                UIPrefabDir = InstanceGet(settings, "UIPrefabDir") as string,
                AssemblyNamesToSearch = (InstanceGet(settings, "AssemblyNamesToSearch") as System.Collections.IEnumerable)?
                    .Cast<string>().ToList() ?? new List<string>()
            };
            return JsonConvert.SerializeObject(snap);
        }

        private static bool ApplyUIKitSettingsJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            UIKitSettingsSnapshot snap;
            try { snap = JsonConvert.DeserializeObject<UIKitSettingsSnapshot>(json); }
            catch { return false; }
            if (snap == null) return false;

            var settings = LoadUIKitSettings();
            if (settings == null) return false;

            InstanceSet(settings, "Namespace", snap.Namespace);
            InstanceSet(settings, "UIScriptDir", snap.UIScriptDir);
            InstanceSet(settings, "UIPrefabDir", snap.UIPrefabDir);
            InstanceSet(settings, "AssemblyNamesToSearch", snap.AssemblyNamesToSearch ?? new List<string>());
            SaveUIKitSettings(settings);
            return true;
        }

        // ==================================================================================
        // Per-path dynamic restorer for AssetBundle marking -- ResKitAssetsMenu.MarkAB is a toggle,
        // so restoring must first check the current state before deciding whether to click it again, otherwise the state gets flipped the wrong way.
        // ==================================================================================

        private static string AssetBundleMarkKey(string folderPath) => "qframework.assetBundleMark:" + folderPath;

        private static bool IsAssetBundleMarked(string folderPath)
        {
            var menuType = QType("QFramework.ResKitAssetsMenu");
            var m = menuType?.GetMethod("Marked", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            return m != null && (bool)m.Invoke(null, new object[] { folderPath });
        }

        private static void ApplyAssetBundleMark(string folderPath, bool desired)
        {
            if (IsAssetBundleMarked(folderPath) == desired) return;
            var menuType = QType("QFramework.ResKitAssetsMenu");
            var m = menuType?.GetMethod("MarkAB", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            m?.Invoke(null, new object[] { folderPath });
        }

        private static void EnsureAssetBundleMarkRestorer(string folderPath)
        {
            WorkflowSettingRestorerRegistry.Register(AssetBundleMarkKey(folderPath),
                () => JsonConvert.SerializeObject(IsAssetBundleMarked(folderPath)),
                json => { ApplyAssetBundleMark(folderPath, JsonConvert.DeserializeObject<bool>(json)); return true; });
        }

        // ==================================================================================
        // A. Environment (1 skill) -- qframework_get_status also works when the package is missing
        // ==================================================================================

        [UnitySkill("qframework_get_status",
            "Report QFramework installation status — install kind (Toolkits vs single-file core-only vs none), detected assemblies, package version, editor locale (LocaleKitEditor.IsCN), and ResKit simulation mode. Runs with or without QFramework installed — call this first.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Query,
            Tags = new[] { "qframework", "status", "installed", "check", "environment" },
            Outputs = new[] { "installed", "installKind", "assemblies", "version", "editorLocaleIsCN", "simulationMode" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object GetStatus()
        {
            bool installed = QType(CoreArchitectureTypeName) != null;

            bool hasToolkitsAnchor = QType("QFramework.ArchitectureCodeGenerator") != null
                || QType("QFramework.UIKitSettingData") != null
                || QType("QFramework.ResKitEditorAPI") != null;

            string installKind = !installed ? "none" : (hasToolkitsAnchor ? "toolkits" : "coreOnly");

            var knownAssemblyNames = new[] { "QFramework", "QFramework.CoreKit", "UIKit", "UIKit.Editor", "ResKit", "ResKit.Editor", "AudioKit" };
            var loadedNames = new HashSet<string>(
                AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetName().Name; } catch { return null; } })
                    .Where(n => n != null),
                StringComparer.Ordinal);
            var assemblies = knownAssemblyNames.Where(loadedNames.Contains).ToArray();

            string version = ReadPackageVersion();

            bool? editorLocaleIsCN = null;
            if (QType("QFramework.LocaleKitEditor") != null)
                editorLocaleIsCN = GetEditorLocaleIsCN();

            bool? simulationMode = null;
            if (QType("QFramework.ResKitEditorAPI") != null)
                simulationMode = GetSimulationMode();

            return new
            {
                installed,
                installKind,
                assemblies,
                version,
                editorLocaleIsCN,
                simulationMode,
                docs = DocsUrl,
                note = installed && installKind == "coreOnly"
                    ? "Core QFramework interfaces found but no Toolkits anchor type — architecture codegen, UIKit, ResKit and LocaleKit skills will report MISSING_PACKAGE until the Toolkits are installed."
                    : (installed ? null : "Install via Package Manager (Toolkits, asmdef-based) or by dropping QFramework.cs into Assets/ (single-file core only)."),
                versionNote = (installed && version == null)
                    ? "PackageVersion.json is part of the Toolkits install layout at Assets/QFramework/Framework/PackageVersion.json; single-file or relocated installs have no readable version metadata here."
                    : null
            };
        }

        // ==================================================================================
        // B. Architecture-layer code generation (4 skills, CodeGenKit/Editor/Architecture)
        // ==================================================================================

        [UnitySkill("qframework_list_architecture_code_types",
            "List QFramework ArchitectureCodeType values (Architecture/System/Model/Command/Utility/Query) with whether each supports interface generation and its Architecture registration method name.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Query,
            Tags = new[] { "qframework", "architecture", "codegen", "types" },
            Outputs = new[] { "count", "types" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ListArchitectureCodeTypes()
        {
            var generatorType = QType("QFramework.ArchitectureCodeGenerator");
            var enumType = QType("QFramework.ArchitectureCodeType");
            if (generatorType == null || enumType == null) return NoQFramework("QFramework.ArchitectureCodeGenerator");

            var supportsMethod = generatorType.GetMethod("SupportsInterfaceGeneration", BindingFlags.Public | BindingFlags.Static, null, new[] { enumType }, null);
            var registerMethod = generatorType.GetMethod("GetRegisterMethodName", BindingFlags.Public | BindingFlags.Static, null, new[] { enumType }, null);

            var types = new List<object>();
            foreach (var name in Enum.GetNames(enumType))
            {
                var value = Enum.Parse(enumType, name);
                bool supportsInterface = supportsMethod != null && (bool)supportsMethod.Invoke(null, new[] { value });
                string registerName = registerMethod?.Invoke(null, new[] { value }) as string;
                types.Add(new
                {
                    codeType = name,
                    supportsInterfaceGeneration = supportsInterface,
                    registerMethodName = string.IsNullOrEmpty(registerName) ? null : registerName
                });
            }

            return new { count = types.Count, types };
        }

        [UnitySkill("qframework_preview_architecture_code",
            "Preview the QFramework architecture-layer code (Architecture/System/Model/Command/Utility/Query) that ArchitectureCodeGenerator.CreatePreview would generate for a name, without writing any file.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Query,
            Tags = new[] { "qframework", "architecture", "codegen", "preview" },
            Outputs = new[] { "isValid", "error", "codeType", "className", "namespaceName", "assetPath", "code" },
            RequiresInput = new[] { "codeType", "inputName", "namespaceName", "outputRoot" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object PreviewArchitectureCode(string codeType, string inputName, string namespaceName, string outputRoot, bool generateInterface = false)
        {
            if (Validate.Required(codeType, "codeType") is object codeTypeErr) return codeTypeErr;
            if (Validate.Required(inputName, "inputName") is object inputNameErr) return inputNameErr;
            if (Validate.Required(namespaceName, "namespaceName") is object namespaceErr) return namespaceErr;
            if (Validate.Required(outputRoot, "outputRoot") is object outputRootErr) return outputRootErr;

            var generatorType = QType("QFramework.ArchitectureCodeGenerator");
            var enumType = QType("QFramework.ArchitectureCodeType");
            if (generatorType == null || enumType == null) return NoQFramework("QFramework.ArchitectureCodeGenerator");

            if (!TryParseCodeType(enumType, codeType, out var enumValue, out var typeError)) return typeError;

            var createPreview = generatorType.GetMethod("CreatePreview", BindingFlags.Public | BindingFlags.Static);
            if (createPreview == null) return NoQFramework("QFramework.ArchitectureCodeGenerator.CreatePreview");

            var preview = createPreview.Invoke(null, new object[] { enumValue, inputName, namespaceName, outputRoot, generateInterface });
            bool isValid = InstanceGet(preview, "IsValid") is bool v && v;

            return new
            {
                isValid,
                error = isValid ? null : InstanceGet(preview, "ErrorMessage") as string,
                hint = isValid ? null : UpstreamPreviewValidationHint,
                codeType = InstanceGet(preview, "CodeType")?.ToString(),
                className = InstanceGet(preview, "ClassName") as string,
                namespaceName = InstanceGet(preview, "Namespace") as string,
                assetPath = InstanceGet(preview, "AssetPath") as string,
                code = InstanceGet(preview, "Code") as string
            };
        }

        [UnitySkill("qframework_generate_architecture_code",
            "Generate QFramework architecture-layer code (Architecture/System/Model/Command/Utility/Query) to disk via ArchitectureCodeGenerator.CreatePreview + Generate. Refuses to overwrite an existing file at the target path.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Create,
            Tags = new[] { "qframework", "architecture", "codegen", "generate" },
            Outputs = new[] { "success", "error", "assetPath", "codeType", "className" },
            RequiresInput = new[] { "codeType", "inputName", "namespaceName", "outputRoot" },
            TracksWorkflow = true, MutatesAssets = true, MayTriggerReload = true, RiskLevel = "medium")]
        public static object GenerateArchitectureCode(string codeType, string inputName, string namespaceName, string outputRoot, bool generateInterface = false)
        {
            if (Validate.Required(codeType, "codeType") is object codeTypeErr) return codeTypeErr;
            if (Validate.Required(inputName, "inputName") is object inputNameErr) return inputNameErr;
            if (Validate.Required(namespaceName, "namespaceName") is object namespaceErr) return namespaceErr;
            if (Validate.Required(outputRoot, "outputRoot") is object outputRootErr) return outputRootErr;
            if (Validate.SafePath(outputRoot, "outputRoot") is object safeErr) return safeErr;

            var generatorType = QType("QFramework.ArchitectureCodeGenerator");
            var enumType = QType("QFramework.ArchitectureCodeType");
            if (generatorType == null || enumType == null) return NoQFramework("QFramework.ArchitectureCodeGenerator");

            if (!TryParseCodeType(enumType, codeType, out var enumValue, out var typeError)) return typeError;

            var createPreview = generatorType.GetMethod("CreatePreview", BindingFlags.Public | BindingFlags.Static);
            var generate = generatorType.GetMethod("Generate", BindingFlags.Public | BindingFlags.Static);
            if (createPreview == null || generate == null) return NoQFramework("QFramework.ArchitectureCodeGenerator.Generate");

            var preview = createPreview.Invoke(null, new object[] { enumValue, inputName, namespaceName, outputRoot, generateInterface });
            bool previewValid = InstanceGet(preview, "IsValid") is bool pv && pv;
            var className = InstanceGet(preview, "ClassName") as string;

            if (!previewValid)
            {
                return new
                {
                    success = false,
                    error = InstanceGet(preview, "ErrorMessage") as string,
                    hint = UpstreamPreviewValidationHint,
                    assetPath = InstanceGet(preview, "AssetPath") as string,
                    codeType,
                    className
                };
            }

            var result = generate.Invoke(null, new object[] { preview });
            bool success = InstanceGet(result, "Success") is bool s && s;
            var assetPath = InstanceGet(result, "AssetPath") as string;

            if (!success)
            {
                return new
                {
                    success = false,
                    error = InstanceGet(result, "ErrorMessage") as string,
                    hint = UpstreamGenerateOverwriteHint,
                    assetPath,
                    codeType,
                    className
                };
            }

            AssetDatabase.ImportAsset(assetPath);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);
            SkillsLogger.Log($"[QFramework] generated architecture code: {assetPath}");

            return new
            {
                success = true,
                assetPath,
                codeType,
                className
            };
        }

        private sealed class ArchitectureCodeBatchItem
        {
            public string codeType { get; set; }
            public string name { get; set; }
            public string namespaceName { get; set; }
            public string outputRoot { get; set; }
            public bool generateInterface { get; set; }
        }

        [UnitySkill("qframework_generate_architecture_code_batch",
            "Generate multiple QFramework architecture-layer code files in one request. items: JSON array of {codeType, name, namespaceName, outputRoot, generateInterface}.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Create,
            Tags = new[] { "qframework", "architecture", "codegen", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesAssets = true, MayTriggerReload = true, RiskLevel = "medium")]
        public static object GenerateArchitectureCodeBatch(string items)
        {
            if (Validate.RequiredJsonArray(items, "items") is object err) return err;

            return BatchExecutor.Execute<ArchitectureCodeBatchItem>(items, item =>
            {
                var result = GenerateArchitectureCode(item.codeType, item.name, item.namespaceName, item.outputRoot, item.generateInterface);
                if (SkillResultHelper.TryGetError(result, out var errorText))
                    throw new ArgumentException(errorText);
                return result;
            }, item => item.name);
        }

        // ==================================================================================
        // C. View/panel code generation (2 skills, two phases: write files -> wait for compile -> backfill on domain reload)
        // ==================================================================================

        [UnitySkill("qframework_generate_view_controller_code",
            "Generate the bound-view code for a GameObject's QFramework ViewController component via CodeGenKit.Generate(IBindGroup). This writes the .cs/.Designer.cs files immediately and returns before compilation — a [DidReloadScripts] callback (after the next domain reload) later adds the compiled component to the GameObject.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Create,
            Tags = new[] { "qframework", "codegen", "viewcontroller", "bind" },
            Outputs = new[] { "pendingCompile", "error", "expectedScriptPath", "expectedDesignerScriptPath", "className", "namespaceName", "scriptsFolder" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true, MutatesAssets = true, MayTriggerReload = true, RiskLevel = "medium")]
        public static object GenerateViewControllerCode(string name = null, int instanceId = 0, string path = null, string tag = null, string componentType = null, string entityId = null)
        {
            var viewControllerType = QType("QFramework.ViewController");
            var codeGenKitType = QType("QFramework.CodeGenKit");
            var ibindGroupType = QType("QFramework.IBindGroup");
            if (viewControllerType == null || codeGenKitType == null || ibindGroupType == null)
                return NoQFramework("QFramework.ViewController");

            var (go, findErr) = GameObjectFinder.FindOrError(name, instanceId, path, tag, componentType, entityId);
            if (findErr != null) return findErr;

            var viewController = go.GetComponent(viewControllerType);
            if (viewController == null)
            {
                return new
                {
                    error = $"GameObject '{go.name}' has no QFramework.ViewController component.",
                    hint = "Add a ViewController component (or a subclass of it) to the target GameObject first."
                };
            }

            var scriptsFolder = InstanceGet(viewController, "ScriptsFolder") as string;
            if (string.IsNullOrWhiteSpace(scriptsFolder))
            {
                return new
                {
                    error = "ViewController.ScriptsFolder is empty. QFramework builds the output path as " +
                            "'<ScriptsFolder>/<ScriptName>.cs' with no fallback — generating now would write under the project root.",
                    hint = "Set ScriptsFolder on the ViewController component before generating."
                };
            }

            var className = InstanceGet(viewController, "ScriptName") as string;
            if (string.IsNullOrWhiteSpace(className))
            {
                return new
                {
                    error = "ViewController.ScriptName is empty — QFramework needs a class name to generate code.",
                    hint = "Set ScriptName on the ViewController component before generating."
                };
            }

            var namespaceName = InstanceGet(viewController, "Namespace") as string;

            var generateMethod = codeGenKitType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Generate" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == ibindGroupType);
            if (generateMethod == null) return NoQFramework("QFramework.CodeGenKit.Generate(IBindGroup)");

            var expectedScriptPath = $"{scriptsFolder}/{className}.cs";
            var expectedDesignerScriptPath = $"{scriptsFolder}/{className}.Designer.cs";
            bool scriptExistedBefore = File.Exists(expectedScriptPath);
            bool designerExistedBefore = File.Exists(expectedDesignerScriptPath);

            try
            {
                generateMethod.Invoke(null, new object[] { viewController });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return new { error = $"CodeGenKit.Generate failed: {inner.Message}", exceptionType = inner.GetType().Name };
            }

            if (!scriptExistedBefore && File.Exists(expectedScriptPath))
            {
                AssetDatabase.ImportAsset(expectedScriptPath);
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(expectedScriptPath);
                if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);
            }
            if (!designerExistedBefore && File.Exists(expectedDesignerScriptPath))
            {
                AssetDatabase.ImportAsset(expectedDesignerScriptPath);
                var designerAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(expectedDesignerScriptPath);
                if (designerAsset != null) WorkflowManager.SnapshotCreatedAsset(designerAsset);
            }

            SkillsLogger.Log($"[QFramework] ViewController codegen started for '{className}' — pending compile.");

            return new
            {
                pendingCompile = true,
                expectedScriptPath,
                expectedDesignerScriptPath,
                className,
                namespaceName,
                scriptsFolder,
                note = "CodeGenKit writes the .cs/.Designer.cs files synchronously, then a [DidReloadScripts] callback (fires after the next domain reload) adds the compiled component to the GameObject and copies the ViewController's settings onto it. Re-inspect the GameObject or poll script_get_compile_feedback after compilation finishes."
            };
        }

        [UnitySkill("qframework_generate_ui_panel_code",
            "Generate QFramework UIKit panel + Designer code for a UI prefab via UICodeGenerator.DoCreateCode. The target must already be a regular Prefab asset — QFramework silently no-ops for anything else (Model prefabs, missing assets, non-prefabs), so this skill pre-validates and reports a real error instead.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Create,
            Tags = new[] { "qframework", "uikit", "codegen", "panel", "prefab" },
            Outputs = new[] { "pendingCompile", "error", "expectedScriptPath", "expectedDesignerPath", "prefabPath", "uiScriptDir", "uiPrefabDir", "namespaceName" },
            RequiresInput = new[] { "prefabPath" },
            TracksWorkflow = true, SkipAutoPresnapshot = true,
            MutatesAssets = true, MayTriggerReload = true, RiskLevel = "medium")]
        public static object GenerateUIPanelCode(string prefabPath)
        {
            if (Validate.Required(prefabPath, "prefabPath") is object requiredErr) return requiredErr;
            if (Validate.SafePathExists(prefabPath, "prefabPath") is object existsErr) return existsErr;

            var generatorType = QType("QFramework.UICodeGenerator");
            var settingsType = QType("QFramework.UIKitSettingData");
            if (generatorType == null || settingsType == null) return NoQFramework("QFramework.UICodeGenerator");

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return new { error = $"'{prefabPath}' did not load as a GameObject prefab asset." };

            var prefabAssetType = PrefabUtility.GetPrefabAssetType(prefab);
            if (prefabAssetType != PrefabAssetType.Regular && prefabAssetType != PrefabAssetType.Variant)
            {
                return new
                {
                    error = $"'{prefabPath}' is not a regular Prefab asset (type: {prefabAssetType}). " +
                            "UICodeGenerator.DoCreateCode silently does nothing for anything else.",
                    hint = "Point prefabPath at a plain GameObject Prefab built from a UI hierarchy."
                };
            }

            var settings = LoadUIKitSettings();
            var uiScriptDir = InstanceGet(settings, "UIScriptDir") as string ?? "/Scripts/UI";
            var uiPrefabDir = InstanceGet(settings, "UIPrefabDir") as string ?? "/Art/UIPrefab";
            var namespaceName = InstanceGet(settings, "Namespace") as string;

            var expectedScriptPath = ComputeUiScriptPath(prefabPath, uiPrefabDir, uiScriptDir);
            bool existedBefore = !string.IsNullOrEmpty(expectedScriptPath) && File.Exists(expectedScriptPath);

            // UICodeGenerator rewrites <Panel>.Designer.cs every time (the main .cs is only written when it doesn't already exist) --
            // when it already exists, take a "before" snapshot first; when it doesn't, snapshot it afterward as a newly created asset. Both paths must be undoable.
            var expectedDesignerPath = !string.IsNullOrEmpty(expectedScriptPath) && expectedScriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? expectedScriptPath.Substring(0, expectedScriptPath.Length - 3) + ".Designer.cs"
                : null;
            bool designerExistedBefore = !string.IsNullOrEmpty(expectedDesignerPath) && File.Exists(expectedDesignerPath);
            if (designerExistedBefore && WorkflowManager.IsRecording)
            {
                var existingDesignerAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(expectedDesignerPath);
                if (existingDesignerAsset != null) WorkflowManager.SnapshotObject(existingDesignerAsset);
            }

            var doCreateCode = generatorType.GetMethod("DoCreateCode", BindingFlags.Public | BindingFlags.Static);
            if (doCreateCode == null) return NoQFramework("QFramework.UICodeGenerator.DoCreateCode");

            try
            {
                doCreateCode.Invoke(null, new object[] { new UnityEngine.Object[] { prefab } });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return new { error = $"UICodeGenerator.DoCreateCode failed: {inner.Message}", exceptionType = inner.GetType().Name };
            }

            if (!existedBefore && !string.IsNullOrEmpty(expectedScriptPath) && File.Exists(expectedScriptPath))
            {
                AssetDatabase.ImportAsset(expectedScriptPath);
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(expectedScriptPath);
                if (asset != null) WorkflowManager.SnapshotCreatedAsset(asset);
            }

            if (!string.IsNullOrEmpty(expectedDesignerPath) && File.Exists(expectedDesignerPath))
            {
                AssetDatabase.ImportAsset(expectedDesignerPath);
                if (!designerExistedBefore)
                {
                    var newDesignerAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(expectedDesignerPath);
                    if (newDesignerAsset != null) WorkflowManager.SnapshotCreatedAsset(newDesignerAsset);
                }
            }

            SkillsLogger.Log($"[QFramework] UI panel codegen invoked for '{prefabPath}'.");

            return new
            {
                pendingCompile = true,
                expectedScriptPath,
                expectedDesignerPath,
                prefabPath,
                uiScriptDir,
                uiPrefabDir,
                namespaceName,
                note = "UICodeGenerator writes <Panel>.cs only if it does not already exist, and always rewrites <Panel>.Designer.cs. A compile is needed before the generated panel type is usable."
            };
        }

        private static string ComputeUiScriptPath(string prefabPath, string uiPrefabDir, string uiScriptDir)
        {
            if (string.IsNullOrEmpty(prefabPath)) return null;

            string result;
            if (!string.IsNullOrEmpty(uiPrefabDir) && prefabPath.Contains(uiPrefabDir))
                result = prefabPath.Replace(uiPrefabDir, uiScriptDir);
            else if (prefabPath.Contains("/Resources"))
                result = prefabPath.Replace("/Resources", uiScriptDir);
            else
            {
                var parts = prefabPath.Replace('\\', '/').Split('/');
                var lastDir = parts.Length >= 2 ? parts[parts.Length - 2] : null;
                result = string.IsNullOrEmpty(lastDir) ? prefabPath : prefabPath.Replace("/" + lastDir, uiScriptDir);
            }

            return result.Replace(".prefab", ".cs");
        }

        // ==================================================================================
        // D. UIKit project settings (2 skills, Assets/QFrameworkData/ProjectConfig/ProjectConfig.json)
        // ==================================================================================

        [UnitySkill("qframework_get_uikit_settings",
            "Read QFramework UIKit project settings (Assets/QFrameworkData/ProjectConfig/ProjectConfig.json) — default namespace, UI script/prefab output directories, and the assembly names UICodeGenerator searches for bind types.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Query,
            Tags = new[] { "qframework", "uikit", "settings", "config" },
            Outputs = new[] { "namespaceName", "uiScriptDir", "uiPrefabDir", "assemblyNamesToSearch", "isDefaultNamespace" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object GetUIKitSettings()
        {
            var settingsType = QType("QFramework.UIKitSettingData");
            if (settingsType == null) return NoQFramework("QFramework.UIKitSettingData");

            var settings = LoadUIKitSettings();
            if (settings == null) return NoQFramework("QFramework.UIKitSettingData.Load");

            return new
            {
                namespaceName = InstanceGet(settings, "Namespace") as string,
                uiScriptDir = InstanceGet(settings, "UIScriptDir") as string,
                uiPrefabDir = InstanceGet(settings, "UIPrefabDir") as string,
                assemblyNamesToSearch = (InstanceGet(settings, "AssemblyNamesToSearch") as System.Collections.IEnumerable)?.Cast<string>().ToArray() ?? Array.Empty<string>(),
                isDefaultNamespace = InstanceGet(settings, "IsDefaultNamespace") is bool b && b,
                settingsAssetPath = "Assets/QFrameworkData/ProjectConfig/ProjectConfig.json"
            };
        }

        [UnitySkill("qframework_set_uikit_settings",
            "Write QFramework UIKit project settings and persist to Assets/QFrameworkData/ProjectConfig/ProjectConfig.json. Only the parameters you pass are changed.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Modify,
            Tags = new[] { "qframework", "uikit", "settings", "config", "write" },
            Outputs = new[] { "changed", "namespaceName", "uiScriptDir", "uiPrefabDir", "assemblyNamesToSearch", "isDefaultNamespace" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true)]
        public static object SetUIKitSettings(string namespaceName = null, string uiScriptDir = null, string uiPrefabDir = null, string assemblyNamesToSearch = null)
        {
            var settingsType = QType("QFramework.UIKitSettingData");
            if (settingsType == null) return NoQFramework("QFramework.UIKitSettingData");

            var settings = LoadUIKitSettings();
            if (settings == null) return NoQFramework("QFramework.UIKitSettingData.Load");

            List<string> parsedAssemblyNames = null;
            if (assemblyNamesToSearch != null)
            {
                try { parsedAssemblyNames = JsonConvert.DeserializeObject<List<string>>(assemblyNamesToSearch); }
                catch (Exception ex) { return new { error = $"assemblyNamesToSearch must be a JSON array of strings: {ex.Message}" }; }
            }

            var changed = new List<string>();
            var beforeJson = CaptureUIKitSettingsJson();

            if (namespaceName != null) { InstanceSet(settings, "Namespace", namespaceName); changed.Add("namespaceName"); }
            if (uiScriptDir != null) { InstanceSet(settings, "UIScriptDir", uiScriptDir); changed.Add("uiScriptDir"); }
            if (uiPrefabDir != null) { InstanceSet(settings, "UIPrefabDir", uiPrefabDir); changed.Add("uiPrefabDir"); }
            if (parsedAssemblyNames != null) { InstanceSet(settings, "AssemblyNamesToSearch", parsedAssemblyNames); changed.Add("assemblyNamesToSearch"); }

            if (changed.Count == 0)
            {
                return new
                {
                    changed = Array.Empty<string>(),
                    namespaceName = InstanceGet(settings, "Namespace") as string,
                    uiScriptDir = InstanceGet(settings, "UIScriptDir") as string,
                    uiPrefabDir = InstanceGet(settings, "UIPrefabDir") as string,
                    assemblyNamesToSearch = (InstanceGet(settings, "AssemblyNamesToSearch") as System.Collections.IEnumerable)?.Cast<string>().ToArray() ?? Array.Empty<string>(),
                    isDefaultNamespace = InstanceGet(settings, "IsDefaultNamespace") is bool bNone && bNone,
                    note = "No parameters supplied — nothing was written."
                };
            }

            if (WorkflowManager.IsRecording)
                WorkflowManager.SnapshotSetting(UIKitSettingsKey, beforeJson, "QFramework: UIKit Settings");

            SaveUIKitSettings(settings);
            SkillsLogger.Log($"[QFramework] UIKit settings updated: {string.Join(", ", changed)}");

            return new
            {
                changed = changed.ToArray(),
                namespaceName = InstanceGet(settings, "Namespace") as string,
                uiScriptDir = InstanceGet(settings, "UIScriptDir") as string,
                uiPrefabDir = InstanceGet(settings, "UIPrefabDir") as string,
                assemblyNamesToSearch = (InstanceGet(settings, "AssemblyNamesToSearch") as System.Collections.IEnumerable)?.Cast<string>().ToArray() ?? Array.Empty<string>(),
                isDefaultNamespace = InstanceGet(settings, "IsDefaultNamespace") is bool bSet && bSet
            };
        }

        // ==================================================================================
        // E. ResKit AssetBundle marking (3 skills)
        // ==================================================================================

        [UnitySkill("qframework_mark_asset_bundle",
            "Mark or unmark a project folder as a QFramework ResKit AssetBundle via ResKitAssetsMenu.MarkAB. MarkAB itself toggles — this skill checks the current state first so calling it repeatedly with the same 'marked' value is a no-op.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Modify,
            Tags = new[] { "qframework", "reskit", "assetbundle", "mark" },
            Outputs = new[] { "path", "marked", "assetBundleName", "changed" },
            RequiresInput = new[] { "folderPath" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MarkAssetBundle(string folderPath, bool marked = true)
        {
            if (Validate.SafePathExists(folderPath, "folderPath") is object err) return err;
            if (!AssetDatabase.IsValidFolder(folderPath))
                return new { error = $"'{folderPath}' is not a folder. QFramework marks AssetBundles at folder granularity." };

            var menuType = QType("QFramework.ResKitAssetsMenu");
            if (menuType == null) return NoQFramework("QFramework.ResKitAssetsMenu");

            bool currentlyMarked = IsAssetBundleMarked(folderPath);
            bool changed = currentlyMarked != marked;

            if (changed)
            {
                EnsureAssetBundleMarkRestorer(folderPath);
                if (WorkflowManager.IsRecording)
                    WorkflowManager.SnapshotSetting(AssetBundleMarkKey(folderPath), JsonConvert.SerializeObject(currentlyMarked), $"QFramework: Mark AssetBundle ({folderPath})");
                ApplyAssetBundleMark(folderPath, marked);
            }

            var assetBundleName = new DirectoryInfo(folderPath).Name.Replace(".", "_").ToLowerInvariant();

            return new
            {
                success = true,
                path = folderPath,
                marked,
                assetBundleName,
                changed
            };
        }

        private sealed class AssetBundleMarkBatchItem
        {
            public string folderPath { get; set; }
            public bool marked { get; set; } = true;
        }

        [UnitySkill("qframework_mark_asset_bundle_batch",
            "Mark or unmark multiple project folders as QFramework ResKit AssetBundles in one request. items: JSON array of {folderPath, marked}.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Modify,
            Tags = new[] { "qframework", "reskit", "assetbundle", "mark", "batch" },
            Outputs = new[] { "totalItems", "successCount", "failCount", "results" },
            RequiresInput = new[] { "items" },
            TracksWorkflow = true, MutatesAssets = true)]
        public static object MarkAssetBundleBatch(string items)
        {
            if (Validate.RequiredJsonArray(items, "items") is object err) return err;

            return BatchExecutor.Execute<AssetBundleMarkBatchItem>(items, item =>
            {
                var result = MarkAssetBundle(item.folderPath, item.marked);
                if (SkillResultHelper.TryGetError(result, out var errorText))
                    throw new ArgumentException(errorText);
                return result;
            }, item => item.folderPath);
        }

        [UnitySkill("qframework_list_asset_bundle_marks",
            "List all AssetBundle names registered in AssetDatabase together with the asset paths assigned to each. Includes any AssetBundle name, not only ones created via QFramework's ResKitAssetsMenu.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Query,
            Tags = new[] { "qframework", "reskit", "assetbundle", "list" },
            Outputs = new[] { "count", "assetBundles" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ListAssetBundleMarks()
        {
            var menuType = QType("QFramework.ResKitAssetsMenu");
            if (menuType == null) return NoQFramework("QFramework.ResKitAssetsMenu");

            var names = AssetDatabase.GetAllAssetBundleNames();
            var result = names.Select(n => new
            {
                name = n,
                assetPaths = AssetDatabase.GetAssetPathsFromAssetBundle(n)
            }).ToArray();

            return new
            {
                count = result.Length,
                assetBundles = result,
                note = "This reflects every AssetBundle name registered in AssetDatabase, including ones assigned outside ResKitAssetsMenu.MarkAB."
            };
        }

        // ==================================================================================
        // F. ResKit build options (2 skills, EditorPrefs + ResKitEditorAPI.SimulationMode)
        // ==================================================================================

        [UnitySkill("qframework_get_reskit_build_options",
            "Read QFramework ResKit build-time options: SimulationMode (ResKitEditorAPI.SimulationMode), and the two AssetBundle-build EditorPrefs toggles — append hash to bundle names, auto-generate the resource-name constant class.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Query,
            Tags = new[] { "qframework", "reskit", "assetbundle", "settings", "build" },
            Outputs = new[] { "simulationMode", "appendHash", "autoGenerateClass" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object GetResKitBuildOptions()
        {
            var apiType = QType("QFramework.ResKitEditorAPI");
            var viewType = QType("QFramework.ResKitView");
            if (apiType == null || viewType == null) return NoQFramework("QFramework.ResKitEditorAPI");

            return new
            {
                simulationMode = GetSimulationMode(),
                appendHash = GetResKitEditorPrefBool(AppendHashKey()),
                autoGenerateClass = GetResKitEditorPrefBool(AutoGenerateClassKey())
            };
        }

        [UnitySkill("qframework_set_reskit_build_options",
            "Set QFramework ResKit build-time options. simulationMode goes through ResKitEditorAPI.SimulationMode; appendHash/autoGenerateClass have no public setter in QFramework and are written directly to the EditorPrefs keys ResKitView itself reads (ResKitView.KEY_APPEND_HASH / KEY_AUTOGENERATE_CLASS) — the only write path QFramework exposes for them.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Modify,
            Tags = new[] { "qframework", "reskit", "assetbundle", "settings", "build", "write" },
            Outputs = new[] { "changed", "simulationMode", "appendHash", "autoGenerateClass" },
            TracksWorkflow = true)]
        public static object SetResKitBuildOptions(bool? simulationMode = null, bool? appendHash = null, bool? autoGenerateClass = null)
        {
            var apiType = QType("QFramework.ResKitEditorAPI");
            var viewType = QType("QFramework.ResKitView");
            if (apiType == null || viewType == null) return NoQFramework("QFramework.ResKitEditorAPI");

            var changed = new List<string>();

            if (simulationMode.HasValue)
            {
                if (WorkflowManager.IsRecording)
                    WorkflowManager.SnapshotSetting("qframework.simulationMode", JsonConvert.SerializeObject(GetSimulationMode()), "QFramework: ResKit SimulationMode");
                SetSimulationModeRaw(simulationMode.Value);
                changed.Add("simulationMode");
            }

            if (appendHash.HasValue)
            {
                var key = AppendHashKey();
                if (WorkflowManager.IsRecording)
                    WorkflowManager.SnapshotSetting("qframework.reskitAppendHash", JsonConvert.SerializeObject(GetResKitEditorPrefBool(key)), "QFramework: ResKit AppendHash");
                SetResKitEditorPrefBool(key, appendHash.Value);
                changed.Add("appendHash");
            }

            if (autoGenerateClass.HasValue)
            {
                var key = AutoGenerateClassKey();
                if (WorkflowManager.IsRecording)
                    WorkflowManager.SnapshotSetting("qframework.reskitAutoGenerateClass", JsonConvert.SerializeObject(GetResKitEditorPrefBool(key)), "QFramework: ResKit AutoGenerateClass");
                SetResKitEditorPrefBool(key, autoGenerateClass.Value);
                changed.Add("autoGenerateClass");
            }

            if (changed.Count > 0)
                SkillsLogger.Log($"[QFramework] ResKit build options updated: {string.Join(", ", changed)}");

            return new
            {
                changed = changed.ToArray(),
                simulationMode = GetSimulationMode(),
                appendHash = GetResKitEditorPrefBool(AppendHashKey()),
                autoGenerateClass = GetResKitEditorPrefBool(AutoGenerateClassKey())
            };
        }

        // ==================================================================================
        // G. AssetBundle build/clean (2 skills)
        // ==================================================================================

        [UnitySkill("qframework_build_asset_bundles",
            "Build AssetBundles for a build target via QFramework.BuildScript.BuildAssetBundles. The output directory is fixed by QFramework to AssetBundles/<platform> at the project root, mirrored into StreamingAssets/AssetBundles/<platform> — not configurable through this skill. BLOCKS the Editor main thread for the duration of the build.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Create,
            Tags = new[] { "qframework", "reskit", "assetbundle", "build" },
            Outputs = new[] { "success", "error", "buildTarget", "outputDir", "streamingAssetsDir", "elapsedSeconds" },
            TracksWorkflow = false,
            MutatesAssets = true, SupportsDryRun = false, RiskLevel = "high", LongRunning = true)]
        public static object BuildAssetBundles(string buildTarget = null)
        {
            var buildScriptType = QType("QFramework.BuildScript");
            if (buildScriptType == null) return NoQFramework("QFramework.BuildScript");

            BuildTarget target;
            if (string.IsNullOrWhiteSpace(buildTarget))
            {
                target = EditorUserBuildSettings.activeBuildTarget;
            }
            else if (!Enum.TryParse(buildTarget, true, out target) || !Enum.IsDefined(typeof(BuildTarget), target))
            {
                return new
                {
                    error = $"Unknown buildTarget '{buildTarget}'.",
                    hint = "Omit buildTarget to use EditorUserBuildSettings.activeBuildTarget."
                };
            }

            var method = buildScriptType.GetMethod("BuildAssetBundles", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(BuildTarget) }, null);
            if (method == null) return NoQFramework("QFramework.BuildScript.BuildAssetBundles(BuildTarget)");

            if (EditorApplication.isCompiling)
                return new { error = "Unity is still compiling scripts. Wait for compilation to finish before building AssetBundles." };
            if (BuildPipeline.isBuildingPlayer)
                return new { error = "A player build is already in progress. Wait for it to finish before building AssetBundles." };

            var activeTarget = EditorUserBuildSettings.activeBuildTarget;

            // BuildScript.BuildAssetBundles(buildTarget) internally uses the private GetPlatformName() to compute the output directory,
            // and that method hardcodes a read of EditorUserBuildSettings.activeBuildTarget, completely ignoring the buildTarget parameter passed in --
            // the latter only affects the actual compile target of the BuildPipeline.BuildAssetBundles step below. So the path here must
            // be computed from activeTarget, and an extra warning is given when the passed-in target doesn't match activeTarget.
            var pathHelperType = QType("QFramework.AssetBundlePathHelper");
            string platformName = null;
            try
            {
                var platformMethod = pathHelperType?.GetMethod("GetPlatformForAssetBundles", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(BuildTarget) }, null);
                platformName = platformMethod?.Invoke(null, new object[] { activeTarget }) as string;
            }
            catch (Exception ex)
            {
                SkillsLogger.LogVerbose($"[QFramework] platform name resolution failed: {ex.Message}");
            }
            platformName ??= activeTarget.ToString();

            var outputDir = $"AssetBundles/{platformName}";
            var streamingAssetsDir = $"{Application.streamingAssetsPath}/AssetBundles/{platformName}";
            string pathTargetMismatchWarning = target != activeTarget
                ? $"QFramework's BuildScript.BuildAssetBundles computes its output directory from EditorUserBuildSettings.activeBuildTarget ({activeTarget}), not from the requested buildTarget ({target}). The requested buildTarget only affects the internal BuildPipeline compile step; files were actually written under {outputDir}."
                : null;

            var started = DateTime.UtcNow;
            try
            {
                method.Invoke(null, new object[] { target });
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return new
                {
                    success = false,
                    error = $"BuildAssetBundles failed: {inner.Message}",
                    exceptionType = inner.GetType().Name,
                    buildTarget = target.ToString()
                };
            }

            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            SkillsLogger.Log($"[QFramework] built AssetBundles for {target} in {elapsed:F1}s");

            return new
            {
                success = true,
                buildTarget = target.ToString(),
                outputDir,
                streamingAssetsDir,
                elapsedSeconds = Math.Round(elapsed, 2),
                warning = pathTargetMismatchWarning
            };
        }

        [UnitySkill("qframework_clear_asset_bundles",
            "Delete all built QFramework ResKit AssetBundle output via ResKitEditorAPI.ForceClearAssetBundles — removes the AssetBundles/ folder at the project root and StreamingAssets/AssetBundles. Not reversible.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Delete,
            Tags = new[] { "qframework", "reskit", "assetbundle", "clear", "delete" },
            Outputs = new[] { "success", "error", "clearedDirs" },
            TracksWorkflow = false, MutatesAssets = true, RiskLevel = "high")]
        public static object ClearAssetBundles()
        {
            var apiType = QType("QFramework.ResKitEditorAPI");
            if (apiType == null) return NoQFramework("QFramework.ResKitEditorAPI");

            var method = apiType.GetMethod("ForceClearAssetBundles", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (method == null) return NoQFramework("QFramework.ResKitEditorAPI.ForceClearAssetBundles");

            var projectAssetBundlesDir = "AssetBundles";
            var streamingAssetsDir = $"{Application.streamingAssetsPath}/AssetBundles";

            var cleared = new List<string>();
            if (Directory.Exists(projectAssetBundlesDir)) cleared.Add(projectAssetBundlesDir);
            if (Directory.Exists(streamingAssetsDir)) cleared.Add(streamingAssetsDir);

            try
            {
                method.Invoke(null, null);
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                return new { error = $"ForceClearAssetBundles failed: {inner.Message}", exceptionType = inner.GetType().Name };
            }

            SkillsLogger.Log($"[QFramework] cleared AssetBundle output: {(cleared.Count > 0 ? string.Join(", ", cleared) : "(nothing existed)")}");

            return new
            {
                success = true,
                clearedDirs = cleared.ToArray()
            };
        }

        // ==================================================================================
        // H. Architecture scanning and API docs (2 skills, purely read-only type metadata)
        // ==================================================================================

        private static bool ImplementsGeneric(Type type, Type openGenericInterface)
        {
            return openGenericInterface != null &&
                   type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGenericInterface);
        }

        [UnitySkill("qframework_scan_architecture",
            "Scan loaded assemblies (excluding QFramework's own assemblies and system assemblies) for non-abstract types implementing QFramework's architecture roles — IArchitecture, ISystem, IModel, ICommand/ICommand<T>, IQuery<T>, IController. Read-only type metadata inspection; does not enter Play Mode.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Query,
            Tags = new[] { "qframework", "architecture", "scan", "reflection" },
            Outputs = new[] { "architectures", "systems", "models", "commands", "queries", "controllers", "totalCount" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object ScanArchitecture()
        {
            var iArchitecture = QType("QFramework.IArchitecture");
            var iSystem = QType("QFramework.ISystem");
            var iModel = QType("QFramework.IModel");
            var iCommand = QType("QFramework.ICommand");
            var iCommandGeneric = QType("QFramework.ICommand`1");
            var iQueryGeneric = QType("QFramework.IQuery`1");
            var iController = QType("QFramework.IController");
            if (iArchitecture == null) return NoQFramework("QFramework.IArchitecture");

            var architectures = new List<string>();
            var systems = new List<string>();
            var models = new List<string>();
            var commands = new List<string>();
            var queries = new List<string>();
            var controllers = new List<string>();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName;
                try { asmName = asm.GetName().Name; } catch { continue; }
                if (string.IsNullOrEmpty(asmName)) continue;
                if (asmName.IndexOf("QFramework", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (asmName.StartsWith("Unity", StringComparison.Ordinal)) continue;
                if (asmName.StartsWith("System", StringComparison.Ordinal)) continue;
                if (asmName == "mscorlib" || asmName == "netstandard" || asmName.StartsWith("Mono.", StringComparison.Ordinal)
                    || asmName == "Newtonsoft.Json" || asmName.StartsWith("nunit", StringComparison.OrdinalIgnoreCase))
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var type in types)
                {
                    if (type == null || !type.IsClass || type.IsAbstract) continue;

                    if (iArchitecture.IsAssignableFrom(type)) architectures.Add(type.FullName);
                    if (iSystem != null && iSystem.IsAssignableFrom(type)) systems.Add(type.FullName);
                    if (iModel != null && iModel.IsAssignableFrom(type)) models.Add(type.FullName);

                    bool isCommand = (iCommand != null && iCommand.IsAssignableFrom(type)) || ImplementsGeneric(type, iCommandGeneric);
                    if (isCommand) commands.Add(type.FullName);

                    if (ImplementsGeneric(type, iQueryGeneric)) queries.Add(type.FullName);
                    if (iController != null && iController.IsAssignableFrom(type)) controllers.Add(type.FullName);
                }
            }

            string[] Finalize(List<string> list) => list.Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();

            var architecturesArr = Finalize(architectures);
            var systemsArr = Finalize(systems);
            var modelsArr = Finalize(models);
            var commandsArr = Finalize(commands);
            var queriesArr = Finalize(queries);
            var controllersArr = Finalize(controllers);

            return new
            {
                architectures = architecturesArr,
                systems = systemsArr,
                models = modelsArr,
                commands = commandsArr,
                queries = queriesArr,
                controllers = controllersArr,
                totalCount = architecturesArr.Length + systemsArr.Length + modelsArr.Length + commandsArr.Length + queriesArr.Length + controllersArr.Length
            };
        }

        private sealed class ApiMemberInfo
        {
            public string name;
            public string kind;
            public string descriptionCN;
            public string descriptionEN;
            public string exampleCode;
        }

        private static ApiMemberInfo DescribeApiMember(string name, string kind, MemberInfo member, Type descCNType, Type descENType, Type exampleType)
        {
            string descCN = null, descEN = null, example = null;

            if (descCNType != null && member.GetCustomAttribute(descCNType, false) is object cnAttr)
                descCN = InstanceGet(cnAttr, "Description") as string;
            if (descENType != null && member.GetCustomAttribute(descENType, false) is object enAttr)
                descEN = InstanceGet(enAttr, "Description") as string;
            if (exampleType != null && member.GetCustomAttribute(exampleType, false) is object exAttr)
                example = InstanceGet(exAttr, "Code") as string;

            return new ApiMemberInfo { name = name, kind = kind, descriptionCN = descCN, descriptionEN = descEN, exampleCode = example };
        }

        [UnitySkill("qframework_query_api_docs",
            "Search QFramework's built-in API documentation attributes (ClassAPIAttribute/MethodAPIAttribute/PropertyAPIAttribute + APIDescriptionCN/EN + APIExampleCode) across loaded assemblies. Optional search/groupName/className filters; results are capped by limit to avoid huge payloads.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Query,
            Tags = new[] { "qframework", "api", "docs", "reflection", "search" },
            Outputs = new[] { "count", "truncated", "classes" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object QueryApiDocs(string search = null, string groupName = null, string className = null, int limit = 50)
        {
            var classAttrType = QType("QFramework.ClassAPIAttribute");
            var methodAttrType = QType("QFramework.MethodAPIAttribute");
            var propAttrType = QType("QFramework.PropertyAPIAttribute");
            var descCNType = QType("QFramework.APIDescriptionCNAttribute");
            var descENType = QType("QFramework.APIDescriptionENAttribute");
            var exampleType = QType("QFramework.APIExampleCodeAttribute");
            if (classAttrType == null) return NoQFramework("QFramework.ClassAPIAttribute");

            int effectiveLimit = Math.Min(Math.Max(limit <= 0 ? 50 : limit, 1), 500);

            var matches = new List<object>();
            bool truncated = false;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (matches.Count >= effectiveLimit) { truncated = true; break; }

                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var type in types)
                {
                    if (matches.Count >= effectiveLimit) { truncated = true; break; }

                    if (!(type.GetCustomAttribute(classAttrType, false) is object classAttr)) continue;

                    var displayMenuName = InstanceGet(classAttr, "DisplayMenuName") as string;
                    var groupNameValue = InstanceGet(classAttr, "GroupName") as string;
                    var renderOrder = InstanceGet(classAttr, "RenderOrder");
                    var displayClassName = InstanceGet(classAttr, "DisplayClassName") as string;

                    if (!string.IsNullOrEmpty(groupName) && !string.Equals(groupNameValue, groupName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(className) &&
                        type.Name.IndexOf(className, StringComparison.OrdinalIgnoreCase) < 0 &&
                        (displayClassName == null || displayClassName.IndexOf(className, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;

                    var members = new List<ApiMemberInfo>();
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (methodAttrType != null && method.GetCustomAttribute(methodAttrType, false) == null) continue;
                        members.Add(DescribeApiMember(method.Name, "method", method, descCNType, descENType, exampleType));
                    }
                    foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (propAttrType != null && prop.GetCustomAttribute(propAttrType, false) == null) continue;
                        members.Add(DescribeApiMember(prop.Name, "property", prop, descCNType, descENType, exampleType));
                    }

                    if (!string.IsNullOrEmpty(search))
                    {
                        bool classMatches = (type.FullName?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                             (displayMenuName?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
                        bool anyMemberMatches = members.Any(m => m.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!classMatches && !anyMemberMatches) continue;
                    }

                    matches.Add(new
                    {
                        className = type.FullName,
                        displayClassName,
                        displayMenuName,
                        groupName = groupNameValue,
                        renderOrder,
                        members
                    });
                }
            }

            return new
            {
                count = matches.Count,
                truncated,
                classes = matches
            };
        }

        // ==================================================================================
        // I. LocaleKit (2 skills)
        // ==================================================================================

        [UnitySkill("qframework_set_editor_locale",
            "Set QFramework's editor UI locale via LocaleKitEditor.IsCN (backed by EditorPrefs key EDITOR_CN). Affects QFramework's own editor windows (ResKit, UIKit, etc), not runtime localization.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Modify,
            Tags = new[] { "qframework", "locale", "editor", "language" },
            Outputs = new[] { "isCN", "changed" },
            RequiresInput = new[] { "isCN" },
            TracksWorkflow = true)]
        public static object SetEditorLocale(bool isCN)
        {
            var localeType = QType("QFramework.LocaleKitEditor");
            if (localeType == null) return NoQFramework("QFramework.LocaleKitEditor");

            var propObj = StaticGet(localeType, "IsCN");
            if (propObj == null) return NoQFramework("QFramework.LocaleKitEditor.IsCN");

            bool before = InstanceGet(propObj, "Value") is bool b && b;
            bool changed = before != isCN;

            if (changed)
            {
                if (WorkflowManager.IsRecording)
                    WorkflowManager.SnapshotSetting("qframework.editorLocaleIsCN", JsonConvert.SerializeObject(before), "QFramework: Editor Locale");
                InstanceSet(propObj, "Value", isCN);
            }

            return new { isCN, changed };
        }

        [UnitySkill("qframework_set_language_defines",
            "Replace the QFramework LocaleKit LanguageDefineConfig.LanguageDefines list (Assets/QFrameworkData/LocaleKit/Resources/LanguageDefineConfig.asset). languages: JSON array of QFramework.Language enum names (mirrors UnityEngine.SystemLanguage, e.g. English, ChineseSimplified). Creates the asset if it does not exist yet.",
            Category = SkillCategory.QFramework, Operation = SkillOperation.Modify,
            Tags = new[] { "qframework", "locale", "language", "config" },
            Outputs = new[] { "error", "languages", "assetPath" },
            RequiresInput = new[] { "languages" },
            TracksWorkflow = true, SkipAutoPresnapshot = true, MutatesAssets = true)]
        public static object SetLanguageDefines(string languages)
        {
            if (Validate.RequiredJsonArray(languages, "languages") is object requiredErr) return requiredErr;

            var configType = QType("QFramework.LanguageDefineConfig");
            var defineType = QType("QFramework.LanguageDefine");
            var languageEnumType = QType("QFramework.Language");
            if (configType == null || defineType == null || languageEnumType == null) return NoQFramework("QFramework.LanguageDefineConfig");

            List<string> requestedNames;
            try { requestedNames = JsonConvert.DeserializeObject<List<string>>(languages); }
            catch (Exception ex) { return new { error = $"languages must be a JSON array of strings: {ex.Message}" }; }

            if (requestedNames == null || requestedNames.Count == 0)
                return new { error = "languages must contain at least one QFramework.Language name." };

            var validNames = Enum.GetNames(languageEnumType);
            var parsedValues = new List<object>();
            foreach (var n in requestedNames)
            {
                if (!validNames.Any(v => string.Equals(v, n, StringComparison.OrdinalIgnoreCase)))
                {
                    return new
                    {
                        error = $"Unknown language '{n}'.",
                        errorCode = "SEMANTIC_INVALID",
                        available = validNames,
                        hint = "Use a QFramework.Language enum name (mirrors UnityEngine.SystemLanguage, e.g. English, ChineseSimplified, Japanese)."
                    };
                }
                parsedValues.Add(Enum.Parse(languageEnumType, n, true));
            }

            var defaultProp = configType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
            var configObj = defaultProp?.GetValue(null);
            if (configObj == null) return NoQFramework("QFramework.LanguageDefineConfig.Default");

            if (WorkflowManager.IsRecording && configObj is UnityEngine.Object unityObj)
                WorkflowManager.SnapshotObject(unityObj);

            var listType = typeof(List<>).MakeGenericType(defineType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType);
            var languageField = defineType.GetField("Language", BindingFlags.Public | BindingFlags.Instance);
            foreach (var value in parsedValues)
            {
                var defineInstance = Activator.CreateInstance(defineType);
                languageField?.SetValue(defineInstance, value);
                list.Add(defineInstance);
            }

            InstanceSet(configObj, "LanguageDefines", list);

            var saveMethod = configType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            saveMethod?.Invoke(configObj, null);

            SkillsLogger.Log($"[QFramework] language defines set to: {string.Join(", ", requestedNames)}");

            return new
            {
                languages = requestedNames.ToArray(),
                assetPath = "Assets/QFrameworkData/LocaleKit/Resources/LanguageDefineConfig.asset"
            };
        }
    }
}

// Producer:Betsy
