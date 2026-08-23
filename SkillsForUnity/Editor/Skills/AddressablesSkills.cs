using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

namespace UnitySkills
{
    /// <summary>
    /// Addressables（com.unity.addressables）编辑器技能：资源分组、构建管线与 Profile 管理。
    ///
    /// 该包是可选的，本模块对它保持零直接引用：所有调用都通过反射访问
    /// Unity.Addressables.Editor 程序集，因此无论项目是否安装 Addressables，
    /// UnitySkills 编辑器程序集都能同样编译通过。<c>addressables_check_installed</c>
    /// 两种情况下都可用；其余技能在缺包时一律返回 <see cref="NoAddressables"/>。
    ///
    /// 反射用到的 API 以 com.unity.addressables 2.x 编辑器源码为准
    /// (UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject,
    ///  UnityEditor.AddressableAssets.Settings.AddressableAssetSettings,
    ///  UnityEditor.AddressableAssets.Settings.AddressableAssetGroup,
    ///  UnityEditor.AddressableAssets.Build.AddressableAssetSettingsDefaultObject)。
    /// </summary>
    public static class AddressablesSkills
    {
        private const string EditorAssemblyName  = "Unity.Addressables.Editor";
        private const string PackageId           = "com.unity.addressables";
        private const string DocsUrl             = "https://docs.unity3d.com/Packages/com.unity.addressables@latest";

        // ==================================================================================
        // 反射层 —— 惰性解析 Unity.Addressables.Editor，绝不与之静态链接。
        // ==================================================================================

        private static Assembly _editorAssembly;

        private static Assembly EditorAssembly()
        {
            if (_editorAssembly != null)
                return _editorAssembly;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName;
                try { asmName = asm.GetName().Name; }
                catch { continue; }

                if (string.Equals(asmName, EditorAssemblyName, StringComparison.Ordinal))
                {
                    _editorAssembly = asm;
                    break;
                }
            }
            return _editorAssembly;
        }

        private static bool Installed => EditorAssembly() != null;

        private static Type AddrType(string fullName)
        {
            var asm = EditorAssembly();
            if (asm == null) return null;
            try { return asm.GetType(fullName, false); }
            catch { return null; }
        }

        private static Type DefaultObjectType =>
            AddrType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject");

        /// <summary>取 AddressableAssetSettings 单例；尚未配置时返回 null。</summary>
        private static object GetSettings()
        {
            var t = DefaultObjectType;
            if (t == null) return null;
            try
            {
                var prop = t.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
                return prop?.GetValue(null);
            }
            catch (Exception ex)
            {
                SkillsLogger.LogWarning($"[Addressables] Settings access failed: {ex.Message}");
                return null;
            }
        }

        private static object NoAddressables() => new
        {
            error = $"Addressables package ({PackageId}) is not installed — the 'Unity.Addressables.Editor' " +
                    "assembly could not be resolved. Install it via Window > Package Manager > Add package by name > " +
                    PackageId + ", then create an Addressables Settings asset via Window > Asset Management > Addressables > Groups.",
            errorCode = "MISSING_PACKAGE",
            requiredPackage = PackageId,
            docs = DocsUrl,
            hint = "Call addressables_check_installed first — it is the only skill in this module that works without the package."
        };

        private static object NoSettings() => new
        {
            error = "Addressables package is installed but no AddressableAssetSettings asset was found. " +
                    "Create one via Window > Asset Management > Addressables > Groups, then click 'Create Addressables Settings'.",
            errorCode = "TARGET_NOT_FOUND",
            hint = "After creating settings, retry your original command.",
            // 必须显式声明：交给分类器自动推断时，这段文案会命中 TARGET_NOT_FOUND 的
            // 资源标记分支而给出 asset_find —— 但这里没有资源可找，settings 单例尚不存在，
            // 只能由人（或 Groups 窗口）创建。
            relatedSkills = new[] { "addressables_check_installed" },
            suggestedFixes = new[]
            {
                new
                {
                    action = "confirm",
                    skill = "addressables_check_installed",
                    reason = "Reports installed/configured separately — 'configured:false' means the settings asset must be created from Window > Asset Management > Addressables > Groups before any other Addressables skill can work."
                }
            }
        };

        /// <summary>
        /// 组名未命中，按路由层 1 的错误契约（<c>SkillResultHelper.TryGetErrorContext</c>）封装。
        /// "Group not found: X" 查的是 AddressableAssetSettings 资源内部，不是场景对象；
        /// 而分类器通用的 TARGET_NOT_FOUND 分支会回以 gameobject_find / scene_get_hierarchy，
        /// 把调用方引去 Hierarchy 里找一个只存在于设置资源中的东西。
        /// 这里只声明 relatedSkills/suggestedFixes：推断出的错误码（TARGET_NOT_FOUND）
        /// 与策略（find_target_and_retry）对这条消息本身是正确的。
        /// </summary>
        private static object GroupNotFound(string groupName) => new
        {
            error = $"Group not found: {groupName}",
            relatedSkills = new[] { "addressables_group_list" },
            suggestedFixes = new[]
            {
                new
                {
                    action = "find_target",
                    skill = "addressables_group_list",
                    reason = "Lists the group names that exist in this project's Addressables settings — retry with one of those. Note that addressables_group_create renames on collision (TestGroup -> TestGroup1), so the name on disk may not be the name you asked for."
                }
            }
        };

        /// <summary>
        /// <see cref="GroupNotFound"/> 的 Profile 版本。这里比组名的情况更糟：
        /// "Profile not found: …" 含子串 "file"，会命中分类器的资源标记分支而导向 asset_find，
        /// 让调用方全工程搜索一个其实只是 AddressableAssetProfileSettings 里字符串字段的名字。
        /// </summary>
        private static object ProfileNotFound(string profileName) => new
        {
            error = $"Profile not found: {profileName}",
            relatedSkills = new[] { "addressables_profile_get" },
            suggestedFixes = new[]
            {
                new
                {
                    action = "find_target",
                    skill = "addressables_profile_get",
                    reason = "Returns activeProfile plus the full profiles list from the Addressables settings — retry with a name from that list."
                }
            }
        };

        private static object Prop(object obj, string name)
        {
            if (obj == null) return null;
            try { return obj.GetType().GetProperty(name)?.GetValue(obj); }
            catch { return null; }
        }

        private static T PropT<T>(object obj, string name)
        {
            var v = Prop(obj, name);
            return v is T t ? t : default;
        }

        private static bool IsDefaultGroup(object settings, object group)
        {
            if (ReferenceEquals(Prop(settings, "DefaultGroup"), group))
                return true;

            var method = group?.GetType().GetMethod("IsDefaultGroup", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            return method?.Invoke(group, null) is bool value ? value : PropT<bool>(group, "IsDefaultGroup");
        }

        // ==================================================================================
        // 技能
        // ==================================================================================

        [UnitySkill("addressables_check_installed",
            "Check if Addressables package is installed and configured",
            Category = SkillCategory.Addressables,
            Operation = SkillOperation.Query,
            Tags = new[] { "addressables", "check", "status" },
            Outputs = new[] { "installed", "configured", "version" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object AddressablesCheckInstalled()
        {
            var editorAsm = EditorAssembly();
            bool installed = editorAsm != null;

            if (!installed)
            {
                return new
                {
                    installed = false,
                    configured = false,
                    packageId = PackageId,
                    hint = "Install via Window > Package Manager > Add package by name > " + PackageId
                };
            }

            var settings = GetSettings();
            bool configured = settings != null;

            string version = null;
            try
            {
                version = UnityEditor.PackageManager.PackageInfo.FindForAssembly(editorAsm)?.version;
            }
            catch { }

            return new
            {
                installed = true,
                configured,
                version,
                packageId = PackageId,
                settingsPath = configured ? AssetDatabase.GetAssetPath(settings as UnityEngine.Object) : null,
                hint = configured
                    ? "Addressables is ready to use"
                    : "Create settings via Window > Asset Management > Addressables > Groups"
            };
        }

        [UnitySkill("addressables_group_list",
            "List all Addressables asset groups",
            Category = SkillCategory.Addressables,
            Operation = SkillOperation.Query,
            Tags = new[] { "addressables", "groups", "list" },
            Outputs = new[] { "count", "groups" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object AddressablesGroupList()
        {
            if (!Installed) return NoAddressables();

            var settings = GetSettings();
            if (settings == null) return NoSettings();

            try
            {
                var groupsProp = settings.GetType().GetProperty("groups");
                var groupsList = groupsProp?.GetValue(settings) as System.Collections.IList;

                if (groupsList == null)
                    return new { error = "Failed to retrieve groups list from settings" };

                var results = new List<object>();
                foreach (var group in groupsList)
                {
                    if (group == null) continue;

                    var groupName = PropT<string>(group, "Name") ?? PropT<string>(group, "name");
                    var groupGuid = PropT<string>(group, "Guid");
                    var isDefault = IsDefaultGroup(settings, group);
                    var readOnly = PropT<bool>(group, "ReadOnly");

                    var entriesProp = group.GetType().GetProperty("entries");
                    var entries = entriesProp?.GetValue(group);
                    int entryCount = entries is System.Collections.ICollection collection ? collection.Count : 0;

                    results.Add(new
                    {
                        name = groupName,
                        guid = groupGuid,
                        isDefault,
                        readOnly,
                        entryCount
                    });
                }

                return new
                {
                    success = true,
                    count = results.Count,
                    groups = results
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to list groups: {ex.Message}" };
            }
        }

        [UnitySkill("addressables_group_create",
            "Create a new Addressables asset group",
            Category = SkillCategory.Addressables,
            Operation = SkillOperation.Create,
            Tags = new[] { "addressables", "group", "create" },
            Outputs = new[] { "groupName", "requestedName", "renamed", "guid" },
            // groupName 没有 CLR 默认值，但对 IsParameterRequired 而言无默认值的引用类型算可选，
            // 于是 schema 会声明 required:false，而下面的方法体对缺省和空串都会拒绝。
            // 这里显式声明，让 schema、dryRun 判定与运行时行为三者一致。
            RequiresInput = new[] { "groupName" },
            TracksWorkflow = false)]
        public static object AddressablesGroupCreate(string groupName)
        {
            if (!Installed) return NoAddressables();
            if (string.IsNullOrWhiteSpace(groupName))
                return new { error = "groupName parameter is required" };

            var settings = GetSettings();
            if (settings == null) return NoSettings();

            try
            {
                var createMethod = settings.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method => method.Name == "CreateGroup" && method.GetParameters().Length == 6 &&
                                              method.GetParameters()[4].ParameterType.IsGenericType &&
                                              method.GetParameters()[5].ParameterType == typeof(Type[]));

                if (createMethod == null)
                    return new { error = "CreateGroup method not found on AddressableAssetSettings" };

                var bundledSchemaType = AddrType("UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema");
                var contentUpdateSchemaType = AddrType("UnityEditor.AddressableAssets.Settings.GroupSchemas.ContentUpdateGroupSchema");
                if (bundledSchemaType == null || contentUpdateSchemaType == null)
                    return new { error = "Required Addressables group schema types were not found" };

                object newGroup = createMethod.Invoke(settings, new object[]
                {
                    groupName,
                    false,  // setAsDefaultGroup
                    false,  // readOnly
                    true,   // postEvent
                    null,   // schemasToCopy
                    new[] { bundledSchemaType, contentUpdateSchemaType }
                });

                if (newGroup == null)
                    return new { error = $"Failed to create group '{groupName}'" };

                // CreateGroup 撞名时不会失败，而是追加计数器去重（"TestGroup" -> "TestGroup1"），
                // 所以请求的名字未必就是落盘的名字。必须从返回的 group 对象上读回真实名字，
                // 否则回显的名字 addressables_group_add_entry / _delete 都解析不到。
                var actualName = PropT<string>(newGroup, "Name") ?? PropT<string>(newGroup, "name") ?? groupName;
                bool renamed = !string.Equals(actualName, groupName, StringComparison.Ordinal);
                var groupGuid = PropT<string>(newGroup, "Guid");

                EditorUtility.SetDirty(settings as UnityEngine.Object);
                AssetDatabase.SaveAssets();

                return new
                {
                    success = true,
                    groupName = actualName,
                    requestedName = groupName,
                    renamed,
                    guid = groupGuid,
                    note = renamed
                        ? $"A group named '{groupName}' already existed, so Addressables created '{actualName}' instead. Use groupName ('{actualName}') for every follow-up call."
                        : null
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to create group: {ex.Message}" };
            }
        }

        [UnitySkill("addressables_group_add_entry",
            "Add an asset to an Addressables group by asset path",
            Category = SkillCategory.Addressables,
            Operation = SkillOperation.Modify,
            Tags = new[] { "addressables", "group", "entry", "asset" },
            Outputs = new[] { "assetPath", "groupName", "address" },
            RequiresInput = new[] { "assetPath", "groupName" },
            TracksWorkflow = false)]
        public static object AddressablesGroupAddEntry(string assetPath, string groupName, string address = null)
        {
            if (!Installed) return NoAddressables();

            if (string.IsNullOrWhiteSpace(assetPath))
                return new { error = "assetPath parameter is required" };
            if (string.IsNullOrWhiteSpace(groupName))
                return new { error = "groupName parameter is required" };

            var settings = GetSettings();
            if (settings == null) return NoSettings();

            try
            {
                string guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrEmpty(guid))
                    return new { error = $"Asset not found: {assetPath}" };

                var groupsProp = settings.GetType().GetProperty("groups");
                var groupsList = groupsProp?.GetValue(settings) as System.Collections.IList;

                object targetGroup = null;
                foreach (var group in groupsList)
                {
                    if (group == null) continue;
                    var gName = PropT<string>(group, "Name") ?? PropT<string>(group, "name");
                    if (string.Equals(gName, groupName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetGroup = group;
                        break;
                    }
                }

                if (targetGroup == null)
                    return GroupNotFound(groupName);

                var createOrMoveMethod = settings.GetType().GetMethod("CreateOrMoveEntry",
                    BindingFlags.Public | BindingFlags.Instance);

                if (createOrMoveMethod == null)
                    return new { error = "CreateOrMoveEntry method not found on AddressableAssetSettings" };

                object entry = createOrMoveMethod.Invoke(settings, new object[]
                {
                    guid,
                    targetGroup,
                    false,  // readOnly
                    true    // postEvent
                });

                if (entry == null)
                    return new { error = $"Failed to create entry for {assetPath}" };

                if (!string.IsNullOrWhiteSpace(address))
                {
                    var addressProp = entry.GetType().GetProperty("address",
                        BindingFlags.Public | BindingFlags.Instance);
                    addressProp?.SetValue(entry, address);
                }

                var finalAddress = PropT<string>(entry, "address");

                EditorUtility.SetDirty(settings as UnityEngine.Object);
                AssetDatabase.SaveAssets();

                return new
                {
                    success = true,
                    assetPath,
                    groupName,
                    address = finalAddress,
                    guid
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to add entry: {ex.Message}" };
            }
        }

        [UnitySkill("addressables_profile_get",
            "Get the active Addressables profile name and available profiles",
            Category = SkillCategory.Addressables,
            Operation = SkillOperation.Query,
            Tags = new[] { "addressables", "profile", "config" },
            Outputs = new[] { "activeProfile", "profiles" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object AddressablesProfileGet()
        {
            if (!Installed) return NoAddressables();

            var settings = GetSettings();
            if (settings == null) return NoSettings();

            try
            {
                var profileSettings = Prop(settings, "profileSettings");
                if (profileSettings == null)
                    return new { error = "ProfileSettings not found on AddressableAssetSettings" };

                var activeProfileId = PropT<string>(settings, "activeProfileId");

                var getProfileNameMethod = profileSettings.GetType().GetMethod("GetProfileName",
                    BindingFlags.Public | BindingFlags.Instance);

                string activeProfileName = null;
                if (getProfileNameMethod != null && !string.IsNullOrEmpty(activeProfileId))
                {
                    activeProfileName = getProfileNameMethod.Invoke(profileSettings, new object[] { activeProfileId }) as string;
                }

                var profileIdsProp = profileSettings.GetType().GetMethod("GetAllProfileNames",
                    BindingFlags.Public | BindingFlags.Instance);

                var profileNames = new List<string>();
                if (profileIdsProp != null)
                {
                    var names = profileIdsProp.Invoke(profileSettings, null) as System.Collections.IList;
                    if (names != null)
                    {
                        foreach (var name in names)
                        {
                            if (name is string s) profileNames.Add(s);
                        }
                    }
                }

                return new
                {
                    success = true,
                    activeProfile = activeProfileName ?? activeProfileId,
                    activeProfileId,
                    profiles = profileNames
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to get profile info: {ex.Message}" };
            }
        }

        [UnitySkill("addressables_profile_set",
            "Set the active Addressables profile by name",
            Category = SkillCategory.Addressables,
            Operation = SkillOperation.Modify,
            Tags = new[] { "addressables", "profile", "config" },
            Outputs = new[] { "activeProfile", "changed" },
            RequiresInput = new[] { "profileName" },
            TracksWorkflow = false)]
        public static object AddressablesProfileSet(string profileName)
        {
            if (!Installed) return NoAddressables();

            if (string.IsNullOrWhiteSpace(profileName))
                return new { error = "profileName parameter is required" };

            var settings = GetSettings();
            if (settings == null) return NoSettings();

            try
            {
                var profileSettings = Prop(settings, "profileSettings");
                if (profileSettings == null)
                    return new { error = "ProfileSettings not found on AddressableAssetSettings" };

                var getProfileIdMethod = profileSettings.GetType().GetMethod("GetProfileId",
                    BindingFlags.Public | BindingFlags.Instance);

                if (getProfileIdMethod == null)
                    return new { error = "GetProfileId method not found" };

                string profileId = getProfileIdMethod.Invoke(profileSettings, new object[] { profileName }) as string;

                if (string.IsNullOrEmpty(profileId))
                    return ProfileNotFound(profileName);

                var previousProfileId = PropT<string>(settings, "activeProfileId");
                bool changed = !string.Equals(previousProfileId, profileId, StringComparison.Ordinal);

                var activeProfileIdProp = settings.GetType().GetProperty("activeProfileId",
                    BindingFlags.Public | BindingFlags.Instance);
                activeProfileIdProp?.SetValue(settings, profileId);

                EditorUtility.SetDirty(settings as UnityEngine.Object);
                AssetDatabase.SaveAssets();

                return new
                {
                    success = true,
                    activeProfile = profileName,
                    profileId,
                    changed
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to set profile: {ex.Message}" };
            }
        }

        [UnitySkill("addressables_build",
            "Build Addressables content for the current build target",
            Category = SkillCategory.Addressables,
            Operation = SkillOperation.Execute,
            Tags = new[] { "addressables", "build", "content" },
            Outputs = new[] { "success", "duration", "error" },
            TracksWorkflow = false,
            MayTriggerReload = false,
            RiskLevel = "medium",
            LongRunning = true)]
        public static object AddressablesBuild()
        {
            if (!Installed) return NoAddressables();

            var settings = GetSettings();
            if (settings == null) return NoSettings();

            try
            {
                var buildMethodType = AddrType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings");
                if (buildMethodType == null)
                    return new { error = "AddressableAssetSettings type not found" };

                var buildMethod = buildMethodType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method => method.Name == "BuildPlayerContent" && method.GetParameters().Length == 1 &&
                                              method.GetParameters()[0].ParameterType.IsByRef);

                if (buildMethod == null)
                    return new { error = "BuildPlayerContent method not found" };

                var startTime = DateTime.UtcNow;

                // 签名为 static void BuildPlayerContent(out AddressablesPlayerBuildResult result)，
                // 反射调用时 out 参数先占位为 null，回填后再从 parameters[0] 读取结果。
                var parameters = new object[] { null };
                buildMethod.Invoke(null, parameters);

                var duration = (DateTime.UtcNow - startTime).TotalSeconds;

                var buildError = PropT<string>(parameters[0], "Error");
                if (!string.IsNullOrEmpty(buildError))
                    return new { success = false, duration, error = buildError };

                return new
                {
                    success = true,
                    duration,
                    message = "Addressables content build completed"
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    success = false,
                    error = $"Build failed: {ex.Message}",
                    details = ex.ToString()
                };
            }
        }

        [UnitySkill("addressables_group_delete",
            "Delete an Addressables asset group by name",
            Category = SkillCategory.Addressables,
            Operation = SkillOperation.Delete,
            Tags = new[] { "addressables", "group", "delete" },
            Outputs = new[] { "groupName", "deleted" },
            RequiresInput = new[] { "groupName" },
            TracksWorkflow = false,
            RiskLevel = "medium")]
        public static object AddressablesGroupDelete(string groupName)
        {
            if (!Installed) return NoAddressables();

            if (string.IsNullOrWhiteSpace(groupName))
                return new { error = "groupName parameter is required" };

            var settings = GetSettings();
            if (settings == null) return NoSettings();

            try
            {
                var groupsProp = settings.GetType().GetProperty("groups");
                var groupsList = groupsProp?.GetValue(settings) as System.Collections.IList;

                object targetGroup = null;
                foreach (var group in groupsList)
                {
                    if (group == null) continue;
                    var gName = PropT<string>(group, "Name") ?? PropT<string>(group, "name");
                    if (string.Equals(gName, groupName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetGroup = group;
                        break;
                    }
                }

                if (targetGroup == null)
                    return GroupNotFound(groupName);

                var isDefault = IsDefaultGroup(settings, targetGroup);
                if (isDefault)
                    return new { error = $"Cannot delete default group: {groupName}" };

                var removeMethod = settings.GetType().GetMethod("RemoveGroup",
                    BindingFlags.Public | BindingFlags.Instance);

                if (removeMethod == null)
                    return new { error = "RemoveGroup method not found on AddressableAssetSettings" };

                removeMethod.Invoke(settings, new object[] { targetGroup });

                EditorUtility.SetDirty(settings as UnityEngine.Object);
                AssetDatabase.SaveAssets();

                return new
                {
                    success = true,
                    groupName,
                    deleted = true
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to delete group: {ex.Message}" };
            }
        }
    }
}

// Producer:Betsy
