using UnityEngine;
using UnityEditor;
using System.Linq;

namespace UnitySkills
{
    /// <summary>
    /// 包管理技能 - AI 可调用的 Package Manager 操作
    /// </summary>
    public static class PackageSkills
    {
        [UnitySkill("package_list", "List all installed packages")]
        public static object PackageList()
        {
            var packages = PackageManagerHelper.InstalledPackages;
            if (packages == null)
                return new { error = "Package list not ready. Call package_refresh first." };

            var list = packages.Values.Select(p => new { name = p.name, version = p.version, displayName = p.displayName }).ToList();
            return new { success = true, count = list.Count, packages = list };
        }

        [UnitySkill("package_check", "Check if a package is installed. Returns version if installed.")]
        public static object PackageCheck(string packageId)
        {
            if (Validate.Required(packageId, "packageId") is object err) return err;

            var installed = PackageManagerHelper.IsPackageInstalled(packageId);
            var version = PackageManagerHelper.GetInstalledVersion(packageId);
            return new { packageId, installed, version };
        }

        [UnitySkill("package_install", "Install a package. version is optional.")]
        public static object PackageInstall(string packageId, string version = null)
        {
            if (Validate.Required(packageId, "packageId") is object err) return err;

            string resultMsg = null;
            bool? resultSuccess = null;

            PackageManagerHelper.InstallPackage(packageId, version, (success, msg) =>
            {
                resultSuccess = success;
                resultMsg = msg;
            });

            // 返回异步状态提示
            return new {
                success = true,
                message = $"Installing {packageId}" + (version != null ? $"@{version}" : "") + "... Check Unity console for progress.",
                async = true
            };
        }

        [UnitySkill("package_remove", "Remove an installed package.")]
        public static object PackageRemove(string packageId)
        {
            if (Validate.Required(packageId, "packageId") is object err) return err;

            if (!PackageManagerHelper.IsPackageInstalled(packageId))
                return new { error = $"Package {packageId} is not installed" };

            PackageManagerHelper.RemovePackage(packageId, (success, msg) =>
            {
                if (success)
                    Debug.Log($"[PackageSkills] Removed {packageId}");
                else
                    Debug.LogError($"[PackageSkills] Failed to remove {packageId}: {msg}");
            });

            return new {
                success = true,
                message = $"Removing {packageId}... Check Unity console for progress.",
                async = true
            };
        }

        [UnitySkill("package_refresh", "Refresh the installed package list cache.")]
        public static object PackageRefresh()
        {
            if (PackageManagerHelper.IsRefreshing)
                return new { success = true, message = "Already refreshing..." };

            PackageManagerHelper.RefreshPackageList(success =>
            {
                if (success)
                    Debug.Log("[PackageSkills] Package list refreshed");
                else
                    Debug.LogError("[PackageSkills] Failed to refresh package list");
            });

            return new { success = true, message = "Refreshing package list..." };
        }

        [UnitySkill("package_install_cinemachine", "Install Cinemachine. version: 2 or 3 (default 3). CM3 auto-installs Splines dependency.")]
        public static object PackageInstallCinemachine(int version = 3)
        {
            var useV3 = version >= 3;
            var targetVersion = useV3 ? PackageManagerHelper.Cinemachine3Version : PackageManagerHelper.Cinemachine2Version;

            // 检查是否已安装
            var status = PackageManagerHelper.GetCinemachineStatus();
            if (status.installed)
            {
                if ((useV3 && status.isVersion3) || (!useV3 && !status.isVersion3))
                    return new { success = true, message = $"Cinemachine {status.version} is already installed." };
            }

            PackageManagerHelper.InstallCinemachine(useV3, (success, msg) =>
            {
                if (success)
                    Debug.Log($"[PackageSkills] Cinemachine {msg} installed successfully");
                else
                    Debug.LogError($"[PackageSkills] Failed to install Cinemachine: {msg}");
            });

            var depMsg = useV3 ? " (with Splines dependency)" : "";
            return new {
                success = true,
                message = $"Installing Cinemachine {targetVersion}{depMsg}... Check Unity console for progress.",
                async = true
            };
        }

        [UnitySkill("package_get_cinemachine_status", "Get Cinemachine installation status.")]
        public static object PackageGetCinemachineStatus()
        {
            var status = PackageManagerHelper.GetCinemachineStatus();
            var splinesInstalled = PackageManagerHelper.IsPackageInstalled(PackageManagerHelper.SplinesPackageId);
            var splinesVersion = PackageManagerHelper.GetInstalledVersion(PackageManagerHelper.SplinesPackageId);

            return new {
                cinemachine = new {
                    installed = status.installed,
                    version = status.version,
                    isVersion3 = status.isVersion3
                },
                splines = new {
                    installed = splinesInstalled,
                    version = splinesVersion
                }
            };
        }
    }
}
